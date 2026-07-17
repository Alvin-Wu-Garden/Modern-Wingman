using AgentService.Domain.Models;
using AgentService.Infrastructure.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

namespace AgentService.UnitTests;

public sealed class RoslynCodeAnalyzerTests : IDisposable
{
    private readonly string _root;

    public RoslynCodeAnalyzerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mw-roslyn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteCs(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task SyntheticWorkspaceCache_PreservesCrossProjectConfidenceAndReason()
    {
        var contractsDirectory = Path.Combine(_root, "Contracts");
        var appDirectory = Path.Combine(_root, "App");
        Directory.CreateDirectory(contractsDirectory);
        Directory.CreateDirectory(appDirectory);
        // Deliberately malformed project files force the documented restore-free
        // synthetic fallback while still defining two project ownership boundaries.
        File.WriteAllText(Path.Combine(contractsDirectory, "Contracts.csproj"), "<Project>");
        File.WriteAllText(Path.Combine(appDirectory, "App.csproj"), "<Project>");
        var contract = Path.Combine(contractsDirectory, "Contract.cs");
        var consumer = Path.Combine(appDirectory, "Consumer.cs");
        File.WriteAllText(contract, "namespace Contracts; public sealed class Contract { public void Execute() { } }");
        File.WriteAllText(consumer, "namespace App; public sealed class Consumer { public void Run() => new Contracts.Contract().Execute(); }");

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var cold = await analyzer.AnalyzeAsync(_root, [contract, consumer]);
        var cached = await analyzer.AnalyzeAsync(_root, [contract, consumer]);

        var coldEdge = Assert.Single(cold.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey.Contains("Consumer.Run", StringComparison.Ordinal) &&
            edge.TargetKey.Contains("Contract.Execute", StringComparison.Ordinal));
        var cachedEdge = Assert.Single(cached.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey == coldEdge.SourceKey &&
            edge.TargetKey == coldEdge.TargetKey);

        Assert.Equal(GraphConfidence.Heuristic, coldEdge.Confidence);
        Assert.Equal(coldEdge.Confidence, cachedEdge.Confidence);
        Assert.Equal(coldEdge.Reason, cachedEdge.Reason);
    }

    [Fact]
    public async Task ExtractsTypesMethodsAndCalls()
    {
        var file = WriteCs("OrderService.cs", """
            namespace Acme.Orders;

            public class OrderCalculator
            {
                public double ApplyDiscount(double amount) => amount * 0.9;
            }

            public class OrderService
            {
                private readonly OrderCalculator _calc = new();

                public double FinalPrice(double amount)
                {
                    return _calc.ApplyDiscount(amount);
                }
            }
            """);

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        // Types
        var types = result.Nodes.Where(n => n.Kind == CodeNodeKind.Type).ToList();
        Assert.Contains(types, t => t.Name == "OrderCalculator");
        Assert.Contains(types, t => t.Name == "OrderService");

        // Namespace containment
        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Contains && e.SourceKey == "ns:Acme.Orders");

        // CALLS: FinalPrice → ApplyDiscount
        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Calls &&
            e.SourceKey.Contains("FinalPrice") &&
            e.TargetKey.Contains("ApplyDiscount"));
    }

    [Fact]
    public async Task ExtractsInheritanceAndInterfaces()
    {
        var file = WriteCs("Hierarchy.cs", """
            namespace Acme;

            public interface IRepository { }
            public abstract class BaseRepository { }
            public class UserRepository : BaseRepository, IRepository { }
            """);

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Inherits &&
            e.SourceKey.Contains("UserRepository") &&
            e.TargetKey.Contains("BaseRepository"));
        Assert.Contains(result.Edges, e =>
            e.Kind == CodeEdgeKind.Implements &&
            e.SourceKey.Contains("UserRepository") &&
            e.TargetKey.Contains("IRepository"));
    }

    [Fact]
    public async Task ExtractsDocComments()
    {
        var file = WriteCs("Documented.cs", """
            namespace Acme;

            /// <summary>訂單折扣計算器。</summary>
            public class Discounter
            {
                /// <summary>套用九折。</summary>
                public double Apply(double x) => x * 0.9;
            }
            """);

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        var type = result.Nodes.First(n => n.Kind == CodeNodeKind.Type && n.Name == "Discounter");
        Assert.Contains("訂單折扣計算器", type.DocComment);
    }

    [Fact]
    public async Task InvalidFileIsSkippedGracefully()
    {
        var good = WriteCs("Good.cs", "namespace A; public class B { }");
        var missing = Path.Combine(_root, "DoesNotExist.cs");

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [good, missing]);

        Assert.Contains(result.Nodes, n => n.Name == "B");
    }

    [Fact]
    public async Task AmbiguousOverloadCandidates_AreNotGuessedAndDoNotAbortIndexing()
    {
        var file = WriteCs("Ambiguous.cs", """
            namespace Acme;

            public sealed class AmbiguousCalls
            {
                public void Pick(string value) { }
                public void Pick(System.Uri value) { }

                public void Execute()
                {
                    Pick(null);
                }
            }
            """);

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        var execute = result.Nodes.Single(node =>
            node.Kind == CodeNodeKind.Method && node.Name == "Execute");
        Assert.DoesNotContain(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey == execute.Key &&
            edge.TargetKey.Contains(".Pick(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvesCrossFileOverloadsInterfaceImplementationsAndOverrides()
    {
        var contracts = WriteCs("Contracts.cs", """
            namespace Acme.Contracts;

            public interface INotifier
            {
                void Send(string message);
            }

            public abstract class Worker
            {
                public virtual int Process(int value) => value;
            }

            public sealed class NotificationWorker : Worker
            {
                public override int Process(int value) => value + 1;
                public int Process(string value) => value.Length;
            }

            public sealed class Notifier : INotifier
            {
                void INotifier.Send(string message) { }
            }
            """);
        var consumer = WriteCs("Consumer.cs", """
            using Acme.Contracts;

            namespace Acme.Application;

            public sealed class Consumer
            {
                private readonly INotifier _notifier;
                private readonly NotificationWorker _worker;

                public Consumer(INotifier notifier, NotificationWorker worker)
                {
                    _notifier = notifier;
                    _worker = worker;
                }

                public int Execute()
                {
                    _notifier.Send("indexed");
                    return _worker.Process(42);
                }
            }
            """);

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [contracts, consumer]);

        var execute = result.Nodes.Single(n => n.Kind == CodeNodeKind.Method && n.Name == "Execute");
        var processInt = result.Nodes.Single(n =>
            n.Kind == CodeNodeKind.Method &&
            n.Signature is not null &&
            n.Signature.Contains("NotificationWorker.Process(int)", StringComparison.Ordinal));
        var processString = result.Nodes.Single(n =>
            n.Kind == CodeNodeKind.Method &&
            n.Signature is not null &&
            n.Signature.Contains("NotificationWorker.Process(string)", StringComparison.Ordinal));

        // Semantic overload resolution chooses Process(int), not merely the first
        // method with the same name.  The target lives in a different source file.
        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey == execute.Key &&
            edge.TargetKey == processInt.Key);
        Assert.DoesNotContain(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey == execute.Key &&
            edge.TargetKey == processString.Key);

        // Calling through an interface is preserved as an exact call to the
        // interface contract; it must not be guessed as a concrete dispatch.
        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey == execute.Key &&
            edge.TargetKey.Contains("INotifier.Send", StringComparison.Ordinal));

        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Implements &&
            edge.SourceKey.Contains("Notifier", StringComparison.Ordinal) &&
            edge.TargetKey.Contains("INotifier", StringComparison.Ordinal));
        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Inherits &&
            edge.SourceKey.Contains("NotificationWorker", StringComparison.Ordinal) &&
            edge.TargetKey.Contains("Worker", StringComparison.Ordinal));

        // Constructor parameter types are retained as verifiable references.
        var constructor = result.Nodes.Single(n =>
            n.Kind == CodeNodeKind.Method &&
            n.Signature is not null &&
            n.Signature.Contains("Consumer.Consumer(", StringComparison.Ordinal));
        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.References &&
            edge.SourceKey == constructor.Key &&
            edge.TargetKey.Contains("INotifier", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractsAspNetRoutesContractsTestsConfigurationAndProjectMetadata()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="MediatR" Version="12.4.1" /></ItemGroup>
            </Project>
            """);
        var file = WriteCs("OrdersController.cs", """
            using System;
            namespace Acme;

            class RouteAttribute(string route) : Attribute { }
            class HttpPostAttribute(string route) : Attribute { }
            class FactAttribute : Attribute { }
            class Request { }
            class Response { }

            [Route("api/[controller]")]
            class OrdersController
            {
                [HttpPost("{id}")]
                public Response Update(Request request) => new();
            }

            class SettingsReader
            {
                public string Read(dynamic configuration) => configuration.GetSection("Orders:Mode");
            }

            class OrderTests
            {
                [Fact]
                public void Update_returns_response() => new OrdersController().Update(new Request());
            }
            """);

        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var result = await analyzer.AnalyzeAsync(_root, [file]);

        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Route && node.Name == "POST /api/Orders/{id}");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Endpoint && node.Name == "Update");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.RequestContract && node.Name == "Request");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.ResponseContract && node.Name == "Response");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.ConfigurationKey && node.Name == "Orders:Mode");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Test && node.Name == "Update_returns_response");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Project && node.Name == "App");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Package && node.Name == "MediatR");
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Covers && edge.TargetKey.Contains("Update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractsGroupedMinimalApiRouteAndSemanticHandler()
    {
        var file = WriteCs("MinimalApi.cs", """
            using System;
            namespace Acme;
            class Response { }
            class App
            {
                public App MapGroup(string prefix) => this;
                public void MapGet(string route, Func<Response> handler) { }
            }
            class Bootstrap
            {
                public void Configure(App app)
                {
                    var api = app.MapGroup("/api");
                    api.MapGet("/health", Health);
                }
                public Response Health() => new();
            }
            """);

        var result = await new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance)
            .AnalyzeAsync(_root, [file]);

        var route = Assert.Single(result.Nodes, node =>
            node.Kind == CodeNodeKind.Route && node.Name == "GET /api/health");
        var endpoint = Assert.Single(result.Nodes, node =>
            node.Kind == CodeNodeKind.Endpoint && node.Signature?.Contains("Health", StringComparison.Ordinal) == true);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Handles && edge.SourceKey == route.Key && edge.TargetKey == endpoint.Key);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Handles && edge.SourceKey == endpoint.Key && edge.TargetKey.Contains("Health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyntheticCompilationDowngradesCrossProjectRelations()
    {
        var appDirectory = Path.Combine(_root, "App");
        var libraryDirectory = Path.Combine(_root, "Library");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(libraryDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(libraryDirectory, "Library.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var service = Path.Combine(libraryDirectory, "Service.cs");
        var consumer = Path.Combine(appDirectory, "Consumer.cs");
        File.WriteAllText(service, "namespace Library; public class Service { public void Run() { } }");
        File.WriteAllText(consumer, "namespace App; public class Consumer { public void Execute() => new Library.Service().Run(); }");

        var result = await new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance)
            .AnalyzeAsync(_root, [service, consumer]);

        var call = Assert.Single(result.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey.Contains("Consumer.Execute", StringComparison.Ordinal) &&
            edge.TargetKey.Contains("Service.Run", StringComparison.Ordinal));
        Assert.Equal(GraphConfidence.Heuristic, call.Confidence);
        Assert.Contains("synthetic compilation", call.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BodyOnlyIncrementalUpdate_RebindsOutgoingCalls_AndRejectsDeclarationChanges()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var target = WriteCs("Target.cs", "namespace Acme; public class Target { public void One() { } public void Two() { } }");
        var caller = WriteCs("Caller.cs", "namespace Acme; public class Caller { public void Execute(Target target) { target.One(); } }");
        var analyzer = new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance);
        var full = await analyzer.AnalyzeAsync(_root, [target, caller]);

        File.WriteAllText(caller,
            "namespace Acme; public class Caller { public void Execute(Target target) { target.Two(); } }");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Target.cs"] = Hash(target),
            ["Caller.cs"] = Hash(caller),
        };
        var incremental = await analyzer.AnalyzeBodyChangesAsync(
            _root,
            [caller],
            hashes,
            full.Nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal));

        Assert.True(incremental.Applied, incremental.EscalationReason);
        Assert.NotNull(incremental.Graph);
        Assert.Contains(incremental.Graph.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey.Contains("Caller.Execute", StringComparison.Ordinal) &&
            edge.TargetKey.Contains("Target.Two", StringComparison.Ordinal));
        Assert.DoesNotContain(incremental.Graph.Edges, edge =>
            edge.Kind == CodeEdgeKind.Calls &&
            edge.SourceKey.Contains("Caller.Execute", StringComparison.Ordinal) &&
            edge.TargetKey.Contains("Target.One", StringComparison.Ordinal));

        File.WriteAllText(caller,
            "namespace Acme; public class Caller { public void Renamed(Target target) { target.Two(); } }");
        hashes["Caller.cs"] = Hash(caller);
        var structural = await analyzer.AnalyzeBodyChangesAsync(
            _root,
            [caller],
            hashes,
            full.Nodes.Select(node => node.Key).ToHashSet(StringComparer.Ordinal));
        Assert.False(structural.Applied);
        Assert.Contains("Declaration surface changed", structural.EscalationReason);

        static string Hash(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}
