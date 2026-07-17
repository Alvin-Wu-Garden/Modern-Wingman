using AgentService.Domain.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentService.Infrastructure.CodeAnalysis;

/// <summary>
/// Framework adapters run after the compiler graph has been built.  Keeping these
/// conventions outside <see cref="RoslynCodeAnalyzer"/> prevents ASP.NET and test
/// framework knowledge from leaking into the language analyzer.
/// </summary>
internal static class CSharpFrameworkGraphExtractor
{
    private const string ExtractorVersion = "1.0.0";

    public static CodeAnalysisResult Extract(IReadOnlyList<CSharpAnalysisDocument> documents)
    {
        var result = new CodeAnalysisResult();
        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<(string, string, CodeEdgeKind)>(new EdgeKeyComparer());

        void AddNode(CodeNode node)
        {
            if (nodeKeys.Add(node.Key)) result.Nodes.Add(node);
        }

        void AddEdge(string source, string target, CodeEdgeKind kind, GraphConfidence confidence, string extractor, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target) || source == target ||
                !edgeKeys.Add((source, target, kind))) return;
            result.Edges.Add(new CodeEdge
            {
                SourceKey = source,
                TargetKey = target,
                Kind = kind,
                SourceKind = GraphSourceKind.FrameworkAdapter,
                // The restore-free compilation cannot authenticate framework attribute
                // assemblies.  A short-name convention (HttpGet/Fact/MapGet...) may be
                // user-defined, so it must remain heuristic until an MSBuild-backed
                // adapter proves the framework symbol identity.
                Confidence = confidence is GraphConfidence.Exact or GraphConfidence.Resolved
                    ? GraphConfidence.Heuristic
                    : confidence,
                ExtractorId = extractor,
                ExtractorVersion = ExtractorVersion,
                Reason = reason,
            });
        }

        foreach (var document in documents)
        {
            ExtractAspNet(document, AddNode, AddEdge);
            ExtractTests(document, AddNode, AddEdge);
            ExtractConfiguration(document, AddNode, AddEdge);
            ExtractBackgroundAndEvents(document, AddNode, AddEdge);
        }

        return result;
    }

    private static void ExtractAspNet(
        CSharpAnalysisDocument document,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, GraphConfidence, string, string?> addEdge)
    {
        foreach (var declaration in document.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var controllerSymbol = document.Model.GetDeclaredSymbol(declaration) as INamedTypeSymbol;
            if (controllerSymbol is null) continue;
            var controllerRoute = AttributeString(declaration.AttributeLists, "Route") ?? string.Empty;
            var isController = controllerSymbol.Name.EndsWith("Controller", StringComparison.Ordinal) ||
                               HasAttribute(declaration.AttributeLists, "ApiController") ||
                               declaration.Members.OfType<MethodDeclarationSyntax>()
                                   .Any(method => HttpMappings(method.AttributeLists).Any());
            if (!isController) continue;

            foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (document.Model.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol) continue;
                var mappings = HttpMappings(method.AttributeLists).ToList();
                if (mappings.Count == 0) continue;
                var handlerKey = RoslynCodeAnalyzer.SymbolKey(methodSymbol);
                var endpointKey = $"endpoint:aspnet:{handlerKey}";
                var span = method.GetLocation().GetLineSpan();
                addNode(FrameworkNode(endpointKey, CodeNodeKind.Endpoint, methodSymbol.Name,
                    handlerKey, document.RelativePath, span, "aspnetcore", "csharp", "aspnet-controller"));
                addEdge(endpointKey, handlerKey, CodeEdgeKind.Handles, GraphConfidence.Exact,
                    "aspnet-controller", "Controller action semantic symbol");
                AddContracts(methodSymbol, endpointKey, document, addNode, addEdge, "aspnet-controller");

                foreach (var (httpMethod, methodRoute) in mappings)
                {
                    var template = CombineRoute(controllerRoute, methodRoute)
                        .Replace("[controller]", ControllerToken(controllerSymbol.Name), StringComparison.OrdinalIgnoreCase)
                        .Replace("[action]", methodSymbol.Name, StringComparison.OrdinalIgnoreCase);
                    AddRoute(httpMethod, template, endpointKey, method.GetLocation(), document,
                        addNode, addEdge, "aspnet-controller");
                }
            }
        }

        var groupPrefixes = document.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(variable => (variable.Identifier.ValueText, Invocation: variable.Initializer?.Value as InvocationExpressionSyntax))
            .Where(item => item.Invocation is not null && InvocationName(item.Invocation) == "MapGroup")
            .Select(item => (item.ValueText, Prefix: FirstStringArgument(item.Invocation!)))
            .Where(item => item.Prefix is not null)
            .ToDictionary(item => item.ValueText, item => item.Prefix!, StringComparer.Ordinal);

        foreach (var invocation in document.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var api = InvocationName(invocation);
            var httpMethods = api switch
            {
                "MapGet" => new[] { "GET" }, "MapPost" => new[] { "POST" },
                "MapPut" => new[] { "PUT" }, "MapDelete" => new[] { "DELETE" },
                "MapPatch" => new[] { "PATCH" }, "MapMethods" => MapMethods(invocation),
                _ => [],
            };
            if (httpMethods.Length == 0) continue;
            var route = FirstStringArgument(invocation) ?? string.Empty;
            if (invocation.Expression is MemberAccessExpressionSyntax access &&
                access.Expression is IdentifierNameSyntax receiver &&
                groupPrefixes.TryGetValue(receiver.Identifier.ValueText, out var prefix))
                route = CombineRoute(prefix, route);

            var handlerExpression = invocation.ArgumentList.Arguments.Count > 1
                ? invocation.ArgumentList.Arguments[^1].Expression
                : null;
            var handler = handlerExpression is null ? null :
                document.Model.GetSymbolInfo(handlerExpression).Symbol as IMethodSymbol;
            var handlerKey = handler is null ? null : RoslynCodeAnalyzer.SymbolKey(handler);
            var location = invocation.GetLocation();
            var line = location.GetLineSpan().StartLinePosition.Line + 1;
            var endpointKey = handlerKey is null
                ? $"endpoint:aspnet-minimal:{document.RelativePath}:{line}"
                : $"endpoint:aspnet-minimal:{handlerKey}";
            addNode(FrameworkNode(endpointKey, CodeNodeKind.Endpoint, api, handlerKey ?? api,
                document.RelativePath, location.GetLineSpan(), "aspnetcore", "csharp", "aspnet-minimal-api",
                handler is null ? GraphConfidence.Resolved : GraphConfidence.Exact));
            if (handlerKey is not null)
            {
                addEdge(endpointKey, handlerKey, CodeEdgeKind.Handles, GraphConfidence.Exact,
                    "aspnet-minimal-api", "Minimal API handler semantic symbol");
                AddContracts(handler!, endpointKey, document, addNode, addEdge, "aspnet-minimal-api");
            }
            foreach (var httpMethod in httpMethods)
                AddRoute(httpMethod, route, endpointKey, location, document, addNode, addEdge, "aspnet-minimal-api");
        }
    }

    private static void ExtractTests(
        CSharpAnalysisDocument document,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, GraphConfidence, string, string?> addEdge)
    {
        foreach (var method in document.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var framework = TestFramework(method.AttributeLists);
            if (framework is null || document.Model.GetDeclaredSymbol(method) is not IMethodSymbol symbol) continue;
            var methodKey = RoslynCodeAnalyzer.SymbolKey(symbol);
            var testKey = $"test:{methodKey}";
            var span = method.GetLocation().GetLineSpan();
            addNode(FrameworkNode(testKey, CodeNodeKind.Test, symbol.Name, symbol.ToDisplayString(),
                document.RelativePath, span, framework, "csharp", "csharp-test"));
            addEdge(testKey, methodKey, CodeEdgeKind.Tests, GraphConfidence.Exact, "csharp-test",
                $"Declared by {framework} test attribute");

            foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var called = document.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (called?.Locations.Any(location => location.IsInSource) != true) continue;
                var target = RoslynCodeAnalyzer.SymbolKey(called.OriginalDefinition);
                if (target == methodKey) continue;
                addEdge(testKey, target, CodeEdgeKind.Covers, GraphConfidence.Resolved, "csharp-test",
                    "Direct invocation from test body; this is not runtime coverage");
            }
        }
    }

    private static void ExtractConfiguration(
        CSharpAnalysisDocument document,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, GraphConfidence, string, string?> addEdge)
    {
        foreach (var expression in document.Root.DescendantNodes())
        {
            string? key = expression switch
            {
                ElementAccessExpressionSyntax element when element.ArgumentList.Arguments.Count == 1 =>
                    StringValue(element.ArgumentList.Arguments[0].Expression),
                InvocationExpressionSyntax invocation when InvocationName(invocation) is
                    "GetSection" or "GetValue" or "GetConnectionString" => FirstStringArgument(invocation),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(key)) continue;
            var callable = expression.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
            if (callable is null || document.Model.GetDeclaredSymbol(callable) is not IMethodSymbol owner) continue;
            var configKey = $"config:{key}";
            var span = expression.GetLocation().GetLineSpan();
            addNode(FrameworkNode(configKey, CodeNodeKind.ConfigurationKey, key, key,
                document.RelativePath, span, "microsoft-extensions-configuration", "csharp", "csharp-configuration"));
            addEdge(RoslynCodeAnalyzer.SymbolKey(owner), configKey, CodeEdgeKind.BindsConfiguration,
                GraphConfidence.Resolved, "csharp-configuration", "Literal configuration key read");
        }
    }

    private static void ExtractBackgroundAndEvents(
        CSharpAnalysisDocument document,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, GraphConfidence, string, string?> addEdge)
    {
        foreach (var method in document.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (document.Model.GetDeclaredSymbol(method) is not IMethodSymbol symbol) continue;
            var owner = symbol.ContainingType;
            var interfaces = owner.AllInterfaces.Select(type => type.ToDisplayString()).ToList();
            var attributes = AttributeNames(method.AttributeLists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var methodKey = RoslynCodeAnalyzer.SymbolKey(symbol);
            string? technology = null;
            CodeNodeKind? kind = null;

            if ((symbol.Name == "ExecuteAsync" && InheritsNamed(owner, "BackgroundService")) ||
                (symbol.Name is "StartAsync" or "Execute" &&
                 (interfaces.Any(name => name.EndsWith("IHostedService", StringComparison.Ordinal)) ||
                  interfaces.Any(name => name.EndsWith("IJob", StringComparison.Ordinal)))))
            {
                kind = CodeNodeKind.BackgroundJob;
                technology = interfaces.Any(name => name.EndsWith("IJob", StringComparison.Ordinal)) ? "quartz" : "dotnet-hosted-service";
            }
            else if (attributes.Overlaps(["Function", "FunctionName", "TimerTrigger"]))
            {
                kind = attributes.Contains("TimerTrigger") ? CodeNodeKind.BackgroundJob : CodeNodeKind.EventConsumer;
                technology = "azure-functions";
            }
            else if ((symbol.Name == "Consume" && interfaces.Any(name => name.Contains("IConsumer<", StringComparison.Ordinal))) ||
                     (symbol.Name == "Handle" && interfaces.Any(name => name.Contains("INotificationHandler<", StringComparison.Ordinal))))
            {
                kind = CodeNodeKind.EventConsumer;
                technology = symbol.Name == "Consume" ? "masstransit" : "mediatr";
            }
            if (kind is null) continue;

            var artifactKey = $"{(kind == CodeNodeKind.BackgroundJob ? "job" : "consumer")}:{methodKey}";
            addNode(FrameworkNode(artifactKey, kind.Value, symbol.Name, methodKey, document.RelativePath,
                method.GetLocation().GetLineSpan(), technology!, "csharp", "csharp-entrypoint"));
            addEdge(artifactKey, methodKey, CodeEdgeKind.Handles, GraphConfidence.Resolved,
                "csharp-entrypoint", $"Recognized {technology} entry point");
        }
    }

    private static void AddContracts(
        IMethodSymbol method,
        string endpointKey,
        CSharpAnalysisDocument document,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, GraphConfidence, string, string?> addEdge,
        string extractor)
    {
        foreach (var parameter in method.Parameters)
        {
            var type = UnwrapContractType(parameter.Type);
            if (type is null || IsInfrastructureType(type)) continue;
            var typeKey = RoslynCodeAnalyzer.SymbolKey(type.OriginalDefinition);
            var key = $"request-contract:{typeKey}";
            addNode(FrameworkNode(key, CodeNodeKind.RequestContract, type.Name, type.ToDisplayString(),
                document.RelativePath, method.Locations.First().GetLineSpan(), "aspnetcore", "csharp", extractor));
            addEdge(endpointKey, key, CodeEdgeKind.Consumes, GraphConfidence.Resolved, extractor,
                $"Endpoint parameter {parameter.Name}");
            addEdge(key, typeKey, CodeEdgeKind.References, GraphConfidence.Exact, extractor, "Compiler-resolved contract type");
        }

        var returnType = UnwrapContractType(method.ReturnType);
        if (returnType is null || returnType.SpecialType == SpecialType.System_Void || IsInfrastructureType(returnType)) return;
        var returnTypeKey = RoslynCodeAnalyzer.SymbolKey(returnType.OriginalDefinition);
        var responseKey = $"response-contract:{returnTypeKey}";
        addNode(FrameworkNode(responseKey, CodeNodeKind.ResponseContract, returnType.Name, returnType.ToDisplayString(),
            document.RelativePath, method.Locations.First().GetLineSpan(), "aspnetcore", "csharp", extractor));
        addEdge(endpointKey, responseKey, CodeEdgeKind.Produces, GraphConfidence.Resolved, extractor, "Compiler-resolved return type");
        addEdge(responseKey, returnTypeKey, CodeEdgeKind.References, GraphConfidence.Exact, extractor, "Compiler-resolved contract type");
    }

    private static INamedTypeSymbol? UnwrapContractType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return null;
        while (named.IsGenericType && named.TypeArguments.Length == 1 && named.Name is
               "Task" or "ValueTask" or "ActionResult" or "Results" or "IResult" or "Nullable")
        {
            if (named.TypeArguments[0] is not INamedTypeSymbol inner) break;
            named = inner;
        }
        return named;
    }

    private static bool IsInfrastructureType(INamedTypeSymbol type) =>
        type.SpecialType != SpecialType.None ||
        type.Name is "CancellationToken" or "HttpContext" or "HttpRequest" or "HttpResponse" or "IActionResult" or "IResult";

    private static void AddRoute(
        string httpMethod,
        string template,
        string endpointKey,
        Location location,
        CSharpAnalysisDocument document,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, GraphConfidence, string, string?> addEdge,
        string extractor)
    {
        template = NormalizeRoute(template);
        var routeKey = $"route:{httpMethod}:{template}:{endpointKey}";
        addNode(FrameworkNode(routeKey, CodeNodeKind.Route, $"{httpMethod} {template}",
            $"{httpMethod} {template}", document.RelativePath, location.GetLineSpan(), "aspnetcore", "csharp", extractor));
        addEdge(routeKey, endpointKey, CodeEdgeKind.Handles, GraphConfidence.Exact, extractor,
            "Framework route maps to endpoint");
    }

    private static CodeNode FrameworkNode(
        string key, CodeNodeKind kind, string name, string? signature, string filePath,
        FileLinePositionSpan span, string technology, string language, string extractor,
        GraphConfidence confidence = GraphConfidence.Exact) => new()
    {
        Key = key,
        Kind = kind,
        Name = name,
        Signature = signature,
        FilePath = filePath,
        StartLine = span.StartLinePosition.Line + 1,
        EndLine = span.EndLinePosition.Line + 1,
        Language = language,
        Technology = technology,
        SourceKind = GraphSourceKind.FrameworkAdapter,
        Confidence = confidence is GraphConfidence.Exact or GraphConfidence.Resolved
            ? GraphConfidence.Heuristic
            : confidence,
        ExtractorId = extractor,
        ExtractorVersion = ExtractorVersion,
        Reason = confidence is GraphConfidence.Exact or GraphConfidence.Resolved
            ? "Framework convention matched structurally; package symbol identity was not loaded by MSBuildWorkspace."
            : null,
    };

    private static IEnumerable<(string Method, string Route)> HttpMappings(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attribute in lists.SelectMany(list => list.Attributes))
        {
            var name = SimpleAttributeName(attribute);
            var method = name switch
            {
                "HttpGet" => "GET", "HttpPost" => "POST", "HttpPut" => "PUT",
                "HttpDelete" => "DELETE", "HttpPatch" => "PATCH", "HttpHead" => "HEAD",
                "HttpOptions" => "OPTIONS", _ => null,
            };
            if (method is not null) yield return (method, AttributeFirstString(attribute) ?? string.Empty);
        }
    }

    private static string? TestFramework(SyntaxList<AttributeListSyntax> lists)
    {
        var names = AttributeNames(lists).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Overlaps(["Fact", "Theory"])) return "xunit";
        if (names.Overlaps(["Test", "TestCase", "TestCaseSource"])) return "nunit";
        if (names.Overlaps(["TestMethod", "DataTestMethod"])) return "mstest";
        return null;
    }

    private static IEnumerable<string> AttributeNames(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(list => list.Attributes).Select(SimpleAttributeName);

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        AttributeNames(lists).Contains(name, StringComparer.OrdinalIgnoreCase);

    private static string? AttributeString(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(list => list.Attributes)
            .Where(attribute => string.Equals(SimpleAttributeName(attribute), name, StringComparison.OrdinalIgnoreCase))
            .Select(AttributeFirstString).FirstOrDefault(value => value is not null);

    private static string SimpleAttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^9] : name;
    }

    private static string? AttributeFirstString(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Select(argument => StringValue(argument.Expression)).FirstOrDefault(value => value is not null);

    private static string? FirstStringArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.Select(argument => StringValue(argument.Expression)).FirstOrDefault(value => value is not null);

    private static string? StringValue(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.Token.Value is string value => value,
        _ => null,
    };

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string[] MapMethods(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count < 2) return [];
        var expression = invocation.ArgumentList.Arguments[1].Expression;
        return expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>()
            .Select(literal => literal.Token.Value as string)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string ControllerToken(string typeName) =>
        typeName.EndsWith("Controller", StringComparison.Ordinal) ? typeName[..^"Controller".Length] : typeName;

    private static string CombineRoute(string prefix, string suffix) =>
        string.Join('/', new[] { prefix, suffix }.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim('/')));

    private static string NormalizeRoute(string route) => "/" + route.Trim().Trim('/');

    private static bool InheritsNamed(INamedTypeSymbol type, string name)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (string.Equals(current.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    private sealed class EdgeKeyComparer : IEqualityComparer<(string, string, CodeEdgeKind)>
    {
        public bool Equals((string, string, CodeEdgeKind) x, (string, string, CodeEdgeKind) y) =>
            x.Item3 == y.Item3 && string.Equals(x.Item1, y.Item1, StringComparison.Ordinal) &&
            string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);
        public int GetHashCode((string, string, CodeEdgeKind) value) => HashCode.Combine(value.Item1, value.Item2, value.Item3);
    }
}

internal sealed record CSharpAnalysisDocument(
    SyntaxNode Root,
    SemanticModel Model,
    string RelativePath,
    string FileKey);
