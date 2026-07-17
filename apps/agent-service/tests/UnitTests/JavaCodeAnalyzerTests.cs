using AgentService.Domain.Models;
using AgentService.Infrastructure.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class JavaCodeAnalyzerTests : IDisposable
{
    private readonly string _root;

    public JavaCodeAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mw-java-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteJava(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ExtractsTypeAndPackage()
    {
        var file = WriteJava("OrderService.java", """
            package com.acme.orders;

            public class OrderService {
                public double calculateTotal(int quantity) {
                    return quantity * 10.0;
                }
            }
            """);

        var analyzer = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        var type = result.Nodes.FirstOrDefault(n => n.Kind == CodeNodeKind.Type);
        Assert.NotNull(type);
        Assert.Equal("com.acme.orders.OrderService", type.Key);

        var method = result.Nodes.FirstOrDefault(n => n.Kind == CodeNodeKind.Method);
        Assert.NotNull(method);
        Assert.Equal("calculateTotal", method.Name);

        // namespace CONTAINS type
        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Contains &&
            e.SourceKey == "ns:com.acme.orders" &&
            e.TargetKey == "com.acme.orders.OrderService");
    }

    [Fact]
    public async Task ExtractsInheritanceAndImplements()
    {
        var file = WriteJava("Impl.java", """
            package com.acme;

            class BaseService { }
            interface IOrderService { }
            public class OrderServiceImpl extends BaseService implements IOrderService, AutoCloseable {
                public void close() { }
            }
            """);

        var analyzer = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Inherits && e.TargetKey == "com.acme.BaseService");
        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Implements && e.TargetKey == "com.acme.IOrderService");
        Assert.DoesNotContain(result.Edges, e => e.TargetKey.EndsWith("AutoCloseable", StringComparison.Ordinal));
        var nodeKeys = result.Nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.Edges, edge =>
        {
            Assert.Contains(edge.SourceKey, nodeKeys);
            Assert.Contains(edge.TargetKey, nodeKeys);
        });
    }

    [Fact]
    public async Task ExternalAndJavaLangTypesRemainSignaturesWithoutDanglingEdges()
    {
        var file = WriteJava("ExternalTypes.java", """
            package com.acme;
            import external.api.Contract;
            public class ExternalTypes implements Contract {
                private String name;
                public Contract convert(String value) { return null; }
            }
            """);

        var result = await new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance)
            .AnalyzeAsync(_root, [file]);
        var nodeKeys = result.Nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(result.Edges, edge =>
        {
            Assert.Contains(edge.SourceKey, nodeKeys);
            Assert.Contains(edge.TargetKey, nodeKeys);
        });
        Assert.DoesNotContain(result.Edges, edge =>
            edge.TargetKey is "external.api.Contract" or "com.acme.String");
        Assert.Contains(result.Nodes, node =>
            node.Kind == CodeNodeKind.Field && node.Signature!.Contains("String", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvesCallsWithinProject()
    {
        var f1 = WriteJava("Calculator.java", """
            package com.acme;

            public class Calculator {
                public double applyDiscount(double amount) {
                    return amount * 0.9;
                }
            }
            """);
        var f2 = WriteJava("Checkout.java", """
            package com.acme;

            public class Checkout {
                private Calculator calc = new Calculator();

                public double finalPrice(double amount) {
                    return calc.applyDiscount(amount);
                }
            }
            """);

        var analyzer = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [f1, f2]);

        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Field &&
            node.Key == "com.acme.Checkout.calc");

        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Calls &&
            e.SourceKey.Contains("finalPrice") &&
            e.TargetKey.Contains("applyDiscount"));
    }

    [Fact]
    public async Task ResolvesImportedInterfaceReceiverWithoutProjectWideNameGuessing()
    {
        var contract = WriteJava("IOrderService.java", """
            package com.acme.contract;
            public interface IOrderService { void process(String id); }
            """);
        var implementation = WriteJava("OrderService.java", """
            package com.acme.orders;
            import com.acme.contract.IOrderService;
            public class OrderService implements IOrderService {
                public void process(String id) { }
            }
            """);
        var consumer = WriteJava("Checkout.java", """
            package com.acme.web;
            import com.acme.contract.IOrderService;
            public class Checkout {
                private final IOrderService service;
                public Checkout(IOrderService service) { this.service = service; }
                public void checkout(String id) { service.process(id); }
            }
            """);

        var analyzer = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [contract, implementation, consumer]);

        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey.StartsWith("com.acme.web.Checkout.checkout", StringComparison.Ordinal) &&
            edge.TargetKey.StartsWith("com.acme.contract.IOrderService.process", StringComparison.Ordinal));
        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Implements &&
            edge.SourceKey == "com.acme.orders.OrderService" &&
            edge.TargetKey == "com.acme.contract.IOrderService");
    }

    [Fact]
    public async Task ResolvesOverloadsByArityAndIgnoresCallsInCommentsAndLiterals()
    {
        var file = WriteJava("Calculator.java", """
            package com.acme;
            public class Calculator {
                public int add(int one) { return one; }
                public int add(int one, int two) { return one + two; }
                public int total() {
                    // add(ignored, ignored);
                    String text = "add(ignored, ignored)";
                    return add(1, 2);
                }
            }
            """);

        var analyzer = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);
        var calls = result.Edges.Where(edge => edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey.StartsWith("com.acme.Calculator.total", StringComparison.Ordinal)).ToList();

        var call = Assert.Single(calls);
        Assert.Equal("com.acme.Calculator.add(int,int)", call.TargetKey);
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Method &&
            node.Key == "com.acme.Calculator.add(int)");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Method &&
            node.Key == "com.acme.Calculator.add(int,int)");
        Assert.Equal(GraphConfidence.Heuristic, call.Confidence);
        Assert.False(string.IsNullOrWhiteSpace(call.Reason));
    }

    [Fact]
    public async Task SameArityDifferentParameterTypesDoNotCreateOverrideOrDispatchEdges()
    {
        var file = WriteJava("Hierarchy.java", """
            package com.acme;
            class Base { void process(String value) { } }
            class Child extends Base { void process(int value) { } }
            interface Contract { void handle(String value); }
            class Handler implements Contract { public void handle(int value) { } }
            """);

        var result = await new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance)
            .AnalyzeAsync(_root, [file]);

        Assert.DoesNotContain(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Overrides &&
            edge.SourceKey.Contains("Child.process", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Edges, edge =>
            edge.Kind is CodeEdgeKind.Implements or CodeEdgeKind.DispatchesTo &&
            (edge.SourceKey.Contains("Handler.handle", StringComparison.Ordinal) ||
             edge.TargetKey.Contains("Handler.handle", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExtractsNestedTypesConstructorsAndFieldReferences()
    {
        var file = WriteJava("Container.java", """
            package com.acme;
            public class Container {
                private Helper helper = new Helper();
                public Container() { helper.run(); }
                static class Helper { void run() { } }
            }
            """);

        var analyzer = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Type && node.Key == "com.acme.Container.Helper");
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Contains &&
            edge.SourceKey == "com.acme.Container" && edge.TargetKey == "com.acme.Container.Helper");
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.References &&
            edge.SourceKey == "com.acme.Container" && edge.TargetKey == "com.acme.Container.Helper");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Field &&
            node.Key == "com.acme.Container.helper");
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey.StartsWith("com.acme.Container.Container", StringComparison.Ordinal) &&
            edge.TargetKey == "com.acme.Container.Helper.run()");
    }

    [Fact]
    public async Task ExtractsSpringRoutesEntrypointsTestsConfigurationAndMavenMetadata()
    {
        File.WriteAllText(Path.Combine(_root, "pom.xml"), """
            <project>
              <groupId>com.acme</groupId><artifactId>orders</artifactId>
              <dependencies>
                <dependency><groupId>org.springframework</groupId><artifactId>spring-web</artifactId><version>6.2.0</version></dependency>
              </dependencies>
            </project>
            """);
        var file = WriteJava("OrdersController.java", """
            package com.acme;
            import org.junit.jupiter.api.Test;

            @RequestMapping("/api/orders")
            public class OrdersController {
                @PostMapping("/{id}")
                public Response update(Request request) { return new Response(); }

                @Scheduled(cron = "0 * * * *")
                public void reconcile() { }

                @KafkaListener(topics = "orders")
                public void consume(String message) { }

                public String setting(Environment environment) {
                    return environment.getProperty("orders.mode");
                }

                @Test
                public void updateReturnsResponse() { update(new Request()); }
            }
            class Request { }
            class Response { }
            """);

        var analyzer = new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Route && node.Name == "POST /api/orders/{id}");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Endpoint && node.Name == "update");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.RequestContract && node.Name == "Request");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.ResponseContract && node.Name == "Response");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.BackgroundJob && node.Name == "reconcile");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.EventConsumer && node.Name == "consume");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.ConfigurationKey && node.Name == "orders.mode");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Test && node.Name == "updateReturnsResponse");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Module && node.Name == "orders");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Dependency && node.Name == "spring-web");
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Covers && edge.TargetKey.Contains("update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractsGradleModuleDependencyAndSourceSet()
    {
        File.WriteAllText(Path.Combine(_root, "settings.gradle.kts"), "include(\":app\")");
        var module = Path.Combine(_root, "app");
        Directory.CreateDirectory(module);
        File.WriteAllText(Path.Combine(module, "build.gradle.kts"), """
            dependencies {
                implementation("org.springframework:spring-context:6.2.0")
                testImplementation("org.junit.jupiter:junit-jupiter:5.11.0")
            }
            """);
        var sourceDirectory = Path.Combine(module, "src", "main", "java", "com", "acme");
        Directory.CreateDirectory(sourceDirectory);
        var file = Path.Combine(sourceDirectory, "App.java");
        File.WriteAllText(file, "package com.acme; public class App { }");

        var result = await new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance)
            .AnalyzeAsync(_root, [file]);

        var moduleNode = Assert.Single(result.Nodes, node => node.Kind == CodeNodeKind.Module);
        Assert.Equal("app", moduleNode.Name);
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Dependency &&
            node.Signature == "org.springframework:spring-context:6.2.0");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Dependency &&
            node.Signature == "org.junit.jupiter:junit-jupiter:5.11.0");
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Contains &&
            edge.SourceKey == moduleNode.Key && edge.TargetKey.EndsWith("App.java", StringComparison.Ordinal));
    }
}
