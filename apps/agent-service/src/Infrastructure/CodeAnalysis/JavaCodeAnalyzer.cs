using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace AgentService.Infrastructure.CodeAnalysis;

/// <summary>
/// Java source analyser using a small, dependency-free lexer and structural parser.
///
/// It deliberately does not claim compiler-level binding resolution: no JDK, Maven
/// classpath, or generated sources are required.  Instead, it only emits a CALLS edge
/// when the receiver type, method name and arity identify one project declaration.
/// This is materially safer than the previous project-wide method-name regex match.
/// A future Java language-service adapter can replace this implementation without
/// changing <see cref="ICodeAnalyzer"/>.
/// </summary>
public sealed class JavaCodeAnalyzer(ILogger<JavaCodeAnalyzer> logger) : ICodeAnalyzer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "break", "case", "catch", "class", "continue", "default", "do",
        "else", "enum", "extends", "final", "finally", "for", "if", "implements", "import",
        "instanceof", "interface", "new", "package", "private", "protected", "public", "record",
        "return", "sealed", "static", "strictfp", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "try", "var", "void", "volatile", "while", "yield",
    };

    private static readonly HashSet<string> PrimitiveTypes = new(StringComparer.Ordinal)
    {
        "boolean", "byte", "char", "double", "float", "int", "long", "short", "void", "var",
    };

    public string Language => "java";
    public IReadOnlyList<string> FileExtensions => [".java"];

    public async Task<CodeAnalysisResult> AnalyzeAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken ct = default)
    {
        var result = new CodeAnalysisResult();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var seenEdges = new HashSet<(string Source, string Target, CodeEdgeKind Kind)>();
        var nodeArtifacts = new Dictionary<string, string?>(StringComparer.Ordinal);
        var parsedFiles = new List<JavaFile>();

        void AddNode(CodeNode node)
        {
            if (seenNodes.Add(node.Key))
            {
                result.Nodes.Add(node);
                nodeArtifacts[node.Key] = node.FilePath;
            }
        }

        void AddEdge(string source, string target, CodeEdgeKind kind)
        {
            if (!string.IsNullOrWhiteSpace(target) && source != target && seenEdges.Add((source, target, kind)))
                result.Edges.Add(new CodeEdge
                {
                    SourceKey = source, TargetKey = target, Kind = kind,
                    SourceKind = GraphSourceKind.Ast, Confidence = GraphConfidence.Resolved,
                    ExtractorId = "java-structural-parser", ExtractorVersion = "1.0.0",
                    ArtifactPath = nodeArtifacts.GetValueOrDefault(source),
                });
        }

        void AddEvidenceEdge(CodeEdge edge)
        {
            if (!string.IsNullOrWhiteSpace(edge.TargetKey) && edge.SourceKey != edge.TargetKey &&
                seenEdges.Add((edge.SourceKey, edge.TargetKey, edge.Kind))) result.Edges.Add(edge);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                var relativePath = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                parsedFiles.Add(JavaFile.Parse(relativePath, content));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Java source read or parse failed: {File}", file);
                throw new InvalidDataException(
                    $"Java source could not be read or parsed: {file}", ex);
            }
        }

        var typeByKey = parsedFiles.SelectMany(file => file.Types).ToDictionary(type => type.Key, StringComparer.Ordinal);
        var typesBySimpleName = parsedFiles.SelectMany(file => file.Types)
            .GroupBy(type => type.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        string ResolveType(string rawName, JavaFile file)
        {
            var name = StripTypeSyntax(rawName);
            if (string.IsNullOrWhiteSpace(name) || PrimitiveTypes.Contains(name))
                return string.Empty;

            if (name.Contains('.', StringComparison.Ordinal))
                return name;

            if (file.Imports.TryGetValue(name, out var imported))
                return imported;

            if (typesBySimpleName.TryGetValue(name, out var candidates))
            {
                var samePackage = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Package, file.Package, StringComparison.Ordinal));
                if (samePackage is not null)
                    return samePackage.Key;
                if (candidates.Count == 1)
                    return candidates[0].Key;
            }

            // This can be a same-package type in a partial file set.  Keep the fact
            // as a reference/inheritance target, but never use it to resolve CALLS.
            return string.IsNullOrWhiteSpace(file.Package) ? name : $"{file.Package}.{name}";
        }

        // Pass 1: file/package/type declarations and their structural relationships.
        foreach (var file in parsedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var fileKey = $"file:{file.RelativePath}";
            AddNode(new CodeNode
            {
                Key = fileKey, Kind = CodeNodeKind.File, Name = Path.GetFileName(file.RelativePath),
                FilePath = file.RelativePath, Language = Language,
                SourceKind = GraphSourceKind.Ast, Confidence = GraphConfidence.Exact,
                ExtractorId = "java-structural-parser", ExtractorVersion = "1.0.0",
                ContentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.Content))).ToLowerInvariant(),
            });

            if (!string.IsNullOrEmpty(file.Package))
            {
                AddNode(new CodeNode
                {
                    Key = $"ns:{file.Package}", Kind = CodeNodeKind.Namespace, Name = file.Package, Language = Language,
                    SourceKind = GraphSourceKind.Ast, Confidence = GraphConfidence.Exact,
                    ExtractorId = "java-structural-parser", ExtractorVersion = "1.0.0",
                });
            }

            foreach (var type in file.Types)
            {
                AddNode(new CodeNode
                {
                    Key = type.Key, Kind = CodeNodeKind.Type, Name = type.Name, Signature = type.Key,
                    FilePath = file.RelativePath, StartLine = LineOf(file.Content, file.Tokens[type.NameOffset].Offset),
                    EndLine = LineOf(file.Content, file.Tokens[type.CloseBrace].Offset), Language = Language,
                    SourceKind = GraphSourceKind.Ast, Confidence = GraphConfidence.Resolved,
                    ExtractorId = "java-structural-parser", ExtractorVersion = "1.0.0",
                });
                AddEdge(type.Key, fileKey, CodeEdgeKind.DeclaredIn);
                if (!string.IsNullOrEmpty(file.Package))
                    AddEdge($"ns:{file.Package}", type.Key, CodeEdgeKind.Contains);

                if (type.Parent is not null)
                    AddEdge(type.Parent.Key, type.Key, CodeEdgeKind.Contains);

                if (!string.IsNullOrWhiteSpace(type.Extends))
                {
                    var target = ResolveType(type.Extends, file);
                    type.Extends = target;
                    AddEdge(type.Key, target, CodeEdgeKind.Inherits);
                }
                for (var implementationIndex = 0; implementationIndex < type.Implements.Count; implementationIndex++)
                {
                    var target = ResolveType(type.Implements[implementationIndex], file);
                    type.Implements[implementationIndex] = target;
                    AddEdge(type.Key, target, CodeEdgeKind.Implements);
                }
            }
        }

        // Pass 2: direct members.  This deliberately uses brace scopes rather than
        // regex so comments, strings, anonymous bodies and nested types do not leak
        // into the enclosing declaration.
        foreach (var file in parsedFiles)
        {
            foreach (var type in file.Types)
            {
                ct.ThrowIfCancellationRequested();
                ParseMembers(file, type, ResolveType, AddNode, AddEdge);
            }
        }

        var methodsByType = parsedFiles.SelectMany(file => file.Types).ToDictionary(
            type => type.Key,
            type => type.Methods.GroupBy(method => (method.Name, method.ParameterTypes.Count))
                .ToDictionary(group => group.Key, group => group.ToList()));

        // Pass 3: resolve calls only when a receiver type produces a unique local
        // declaration.  Unknown, external, overloaded and dynamic calls are omitted
        // rather than turned into speculative blast-radius edges.
        foreach (var file in parsedFiles)
        {
            foreach (var type in file.Types)
            {
                foreach (var method in type.Methods)
                {
                    ct.ThrowIfCancellationRequested();
                    ResolveMethodCalls(file, type, method, ResolveType, typeByKey, methodsByType, AddEdge);
                }
            }
        }

        AddOverrideAndDispatchRelationships(parsedFiles, typeByKey, methodsByType, AddEdge);
        Merge(JavaBuildGraphExtractor.Extract(projectRoot, files));
        Merge(ExtractFrameworkGraph(parsedFiles, ResolveType, result.Edges));
        foreach (var edge in result.Edges)
            edge.ArtifactPath ??= nodeArtifacts.GetValueOrDefault(edge.SourceKey)
                ?? nodeArtifacts.GetValueOrDefault(edge.TargetKey);
        // Structural signatures may mention JDK/dependency types that are not project
        // declarations. Keep those signatures, but never publish a dangling edge or
        // pretend an external type is a local graph node.
        result.Edges.RemoveAll(edge =>
            !seenNodes.Contains(edge.SourceKey) || !seenNodes.Contains(edge.TargetKey));
        foreach (var edge in result.Edges.Where(edge =>
                     edge.Kind == CodeEdgeKind.Calls &&
                     string.Equals(edge.ExtractorId, "java-structural-parser", StringComparison.Ordinal)))
        {
            edge.Confidence = GraphConfidence.Heuristic;
            edge.Reason = "Receiver type, method name and arity identify one project declaration; classpath and argument-type binding were not available.";
        }
        CodeAnalysisProvenance.StampEdges(result, DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Java structural analysis completed: {Files} files, {Nodes} nodes, {Edges} edges",
            parsedFiles.Count, result.Nodes.Count, result.Edges.Count);
        return result;

        void Merge(CodeAnalysisResult extracted)
        {
            foreach (var node in extracted.Nodes) AddNode(node);
            foreach (var edge in extracted.Edges) AddEvidenceEdge(edge);
        }
    }

    private static void ParseMembers(
        JavaFile file,
        JavaType type,
        Func<string, JavaFile, string> resolveType,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        var tokens = file.Tokens;
        var depth = 1;
        for (var index = type.OpenBrace + 1; index < type.CloseBrace; index++)
        {
            var token = tokens[index].Text;
            if (token == "{") { depth++; continue; }
            if (token == "}") { depth--; continue; }
            if (depth != 1 || token != "(")
                continue;

            var nameIndex = index - 1;
            if (nameIndex < 0 || !IsIdentifier(tokens[nameIndex].Text) || Keywords.Contains(tokens[nameIndex].Text))
                continue;

            var closeParen = FindMatching(tokens, index, "(", ")");
            if (closeParen < 0)
                continue;
            var bodyStart = FindMethodBodyStart(tokens, closeParen + 1, type.CloseBrace);
            if (bodyStart < 0)
                continue;

            // A call expression followed by a lambda/block must not be mistaken for
            // a method declaration.  Constructors are the one declaration without a
            // return type; all other declarations require a type/modifier before it.
            var methodName = tokens[nameIndex].Text;
            if (!IsPlausibleDeclaration(tokens, type, nameIndex, index))
                continue;

            var parameterTypes = ParseParameterTypes(tokens, index + 1, closeParen - 1);
            var bindingParameterTypes = parameterTypes.Select(parameterType =>
            {
                var erased = StripTypeSyntax(parameterType);
                return PrimitiveTypes.Contains(erased) ? erased : resolveType(parameterType, file);
            }).ToList();
            var methodKey = $"{type.Key}.{methodName}({string.Join(",", parameterTypes)})";
            if (type.Methods.Any(existing => existing.Key == methodKey))
                continue;

            var bodyEnd = tokens[bodyStart].Text == "{"
                ? FindMatching(tokens, bodyStart, "{", "}")
                : bodyStart;
            if (bodyEnd < 0)
                bodyEnd = bodyStart;

            var returnType = methodName == type.Name ? type.Key : FindReturnType(tokens, type, nameIndex);
            var method = new JavaMethod(methodKey, methodName, parameterTypes, bindingParameterTypes,
                returnType, index, bodyStart, bodyEnd,
                ParseVariableTypes(tokens, index + 1, closeParen - 1));
            type.Methods.Add(method);
            addNode(new CodeNode
            {
                Key = method.Key, Kind = CodeNodeKind.Method, Name = method.Name,
                Signature = $"{type.Key}.{method.Name}({string.Join(", ", parameterTypes)})",
                FilePath = file.RelativePath, StartLine = LineOf(file.Content, tokens[nameIndex].Offset),
                EndLine = LineOf(file.Content, tokens[Math.Min(method.BodyEnd, tokens.Count - 1)].Offset), Language = "java",
                SourceKind = GraphSourceKind.Ast, Confidence = GraphConfidence.Resolved,
                ExtractorId = "java-structural-parser", ExtractorVersion = "1.0.0",
            });
            addEdge(type.Key, method.Key, CodeEdgeKind.Contains);

            foreach (var parameterType in parameterTypes)
                addEdge(method.Key, resolveType(parameterType, file), CodeEdgeKind.References);
            if (methodName != type.Name)
                addEdge(method.Key, resolveType(returnType, file), CodeEdgeKind.References);

            // Skip the body; nested blocks are analysed during call resolution.
            index = bodyEnd;
        }

        ParseFields(file, type, resolveType, addNode, addEdge);
    }

    private static void ParseFields(
        JavaFile file,
        JavaType type,
        Func<string, JavaFile, string> resolveType,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        var tokens = file.Tokens;
        var depth = 1;
        var segmentStart = type.OpenBrace + 1;
        for (var index = segmentStart; index < type.CloseBrace; index++)
        {
            if (tokens[index].Text == "{") { depth++; continue; }
            if (tokens[index].Text == "}") { depth--; continue; }
            if (depth != 1 || tokens[index].Text != ";")
                continue;

            var segment = tokens.Skip(segmentStart).Take(index - segmentStart).ToList();
            segmentStart = index + 1;
            var initializer = segment.FindIndex(token => token.Text == "=");
            if (segment.Count < 2 || (initializer < 0 && segment.Any(token => token.Text is "(" or ")")))
                continue;
            var declarationEnd = initializer >= 0 ? initializer : segment.Count;
            var nameIndex = segment.Take(declarationEnd).ToList()
                .FindLastIndex(token => IsIdentifier(token.Text) && !Keywords.Contains(token.Text));
            if (nameIndex <= 0)
                continue;
            var name = segment[nameIndex].Text;
            var typeName = FirstTypeName(segment.Take(nameIndex).ToList());
            var resolvedType = resolveType(typeName, file);
            if (string.IsNullOrWhiteSpace(resolvedType))
                continue;

            var key = $"{type.Key}.{name}";
            type.Fields[name] = resolvedType;
            addNode(new CodeNode
            {
                Key = key, Kind = CodeNodeKind.Field, Name = name, Signature = $"{resolvedType} {name}",
                FilePath = file.RelativePath, StartLine = LineOf(file.Content, segment[Math.Min(nameIndex, segment.Count - 1)].Offset),
                Language = "java",
                SourceKind = GraphSourceKind.Ast, Confidence = GraphConfidence.Resolved,
                ExtractorId = "java-structural-parser", ExtractorVersion = "1.0.0",
            });
            addEdge(type.Key, key, CodeEdgeKind.Contains);
            addEdge(type.Key, resolvedType, CodeEdgeKind.References);
        }
    }

    private static void ResolveMethodCalls(
        JavaFile file,
        JavaType type,
        JavaMethod method,
        Func<string, JavaFile, string> resolveType,
        IReadOnlyDictionary<string, JavaType> typesByKey,
        IReadOnlyDictionary<string, Dictionary<(string Name, int Arity), List<JavaMethod>>> methodsByType,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        if (method.BodyStart < 0 || method.BodyEnd <= method.BodyStart || file.Tokens[method.BodyStart].Text != "{")
            return;

        var variables = new Dictionary<string, string>(type.Fields, StringComparer.Ordinal);
        foreach (var pair in method.ParameterTypes.Zip(method.Parameters, (kind, name) => (kind, name)))
            variables[pair.name] = resolveType(pair.kind, file);
        AddLocalVariables(file.Tokens, method.BodyStart + 1, method.BodyEnd - 1, resolveType, file, variables);

        for (var index = method.BodyStart + 1; index < method.BodyEnd; index++)
        {
            if (file.Tokens[index].Text != "(")
                continue;
            var nameIndex = index - 1;
            if (nameIndex < 0 || !IsIdentifier(file.Tokens[nameIndex].Text) || Keywords.Contains(file.Tokens[nameIndex].Text))
                continue;
            var close = FindMatching(file.Tokens, index, "(", ")");
            if (close < 0 || close > method.BodyEnd)
                continue;

            var calledName = file.Tokens[nameIndex].Text;
            var arity = CountArguments(file.Tokens, index + 1, close - 1);
            var receiverType = ResolveReceiver(file.Tokens, nameIndex, type, variables, resolveType, file);
            foreach (var target in FindUniqueTargets(receiverType, type, calledName, arity, typesByKey, methodsByType))
                addEdge(method.Key, target.Key, CodeEdgeKind.Calls);
            index = close;
        }

        // Java method references (service::run) have no invocation parentheses.
        for (var index = method.BodyStart + 1; index + 1 < method.BodyEnd; index++)
        {
            if (file.Tokens[index].Text != "::" || !IsIdentifier(file.Tokens[index + 1].Text))
                continue;
            var receiver = index > method.BodyStart ? file.Tokens[index - 1].Text : string.Empty;
            var receiverType = variables.TryGetValue(receiver, out var value) ? value : resolveType(receiver, file);
            foreach (var target in FindUniqueTargets(receiverType, type, file.Tokens[index + 1].Text, null, typesByKey, methodsByType))
                addEdge(method.Key, target.Key, CodeEdgeKind.Calls);
        }
    }

    private static IEnumerable<JavaMethod> FindUniqueTargets(
        string? receiverType,
        JavaType currentType,
        string methodName,
        int? arity,
        IReadOnlyDictionary<string, JavaType> typesByKey,
        IReadOnlyDictionary<string, Dictionary<(string Name, int Arity), List<JavaMethod>>> methodsByType)
    {
        var candidates = new List<JavaMethod>();
        var start = string.IsNullOrWhiteSpace(receiverType) ? currentType.Key : receiverType;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Collect(start!, methodName, arity, typesByKey, methodsByType, visited, candidates);
        return candidates.Count == 1 ? candidates : [];
    }

    private static void Collect(
        string typeKey,
        string methodName,
        int? arity,
        IReadOnlyDictionary<string, JavaType> typesByKey,
        IReadOnlyDictionary<string, Dictionary<(string Name, int Arity), List<JavaMethod>>> methodsByType,
        ISet<string> visited,
        ICollection<JavaMethod> candidates)
    {
        if (!visited.Add(typeKey))
            return;
        if (methodsByType.TryGetValue(typeKey, out var bySignature))
        {
            foreach (var pair in bySignature.Where(pair =>
                         string.Equals(pair.Key.Name, methodName, StringComparison.Ordinal) &&
                         (!arity.HasValue || pair.Key.Arity == arity.Value)))
                foreach (var method in pair.Value)
                    candidates.Add(method);
        }
        if (!typesByKey.TryGetValue(typeKey, out var type))
            return;
        if (!string.IsNullOrWhiteSpace(type.Extends))
            Collect(type.Extends!, methodName, arity, typesByKey, methodsByType, visited, candidates);
        foreach (var implemented in type.Implements)
            Collect(implemented, methodName, arity, typesByKey, methodsByType, visited, candidates);
    }

    private static void AddOverrideAndDispatchRelationships(
        IEnumerable<JavaFile> files,
        IReadOnlyDictionary<string, JavaType> typesByKey,
        IReadOnlyDictionary<string, Dictionary<(string Name, int Arity), List<JavaMethod>>> methodsByType,
        Action<string, string, CodeEdgeKind> addEdge)
    {
        foreach (var type in files.SelectMany(file => file.Types))
        {
            foreach (var method in type.Methods)
            {
                if (!string.IsNullOrWhiteSpace(type.Extends) &&
                    UniqueMethod(type.Extends!, method, methodsByType) is { } overridden)
                    addEdge(method.Key, overridden.Key, CodeEdgeKind.Overrides);

                foreach (var interfaceKey in type.Implements)
                {
                    if (!typesByKey.ContainsKey(interfaceKey) ||
                        UniqueMethod(interfaceKey, method, methodsByType) is not { } contract) continue;
                    addEdge(method.Key, contract.Key, CodeEdgeKind.Implements);
                    addEdge(contract.Key, method.Key, CodeEdgeKind.DispatchesTo);
                }
            }
        }
    }

    private static JavaMethod? UniqueMethod(
        string owner,
        JavaMethod implementation,
        IReadOnlyDictionary<string, Dictionary<(string Name, int Arity), List<JavaMethod>>> methodsByType) =>
        methodsByType.TryGetValue(owner, out var members) &&
        members.TryGetValue((implementation.Name, implementation.ParameterTypes.Count), out var candidates)
            ? candidates.SingleOrDefault(candidate => candidate.BindingParameterTypes.SequenceEqual(
                implementation.BindingParameterTypes, StringComparer.Ordinal))
            : null;

    private static CodeAnalysisResult ExtractFrameworkGraph(
        IReadOnlyList<JavaFile> files,
        Func<string, JavaFile, string> resolveType,
        IReadOnlyCollection<CodeEdge> semanticEdges)
    {
        const string version = "1.0.0";
        var result = new CodeAnalysisResult();
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var edges = new HashSet<(string, string, CodeEdgeKind)>();
        void AddNode(CodeNode node) { if (nodes.Add(node.Key)) result.Nodes.Add(node); }
        void AddEdge(string source, string target, CodeEdgeKind kind, string extractor,
            GraphConfidence confidence = GraphConfidence.Resolved, string? reason = null)
        {
            if (string.IsNullOrWhiteSpace(target) || source == target || !edges.Add((source, target, kind))) return;
            result.Edges.Add(new CodeEdge
            {
                SourceKey = source, TargetKey = target, Kind = kind,
                SourceKind = GraphSourceKind.FrameworkAdapter,
                // Annotation short names are structural evidence only until a
                // classpath-aware Java binding adapter verifies their packages.
                Confidence = confidence is GraphConfidence.Exact or GraphConfidence.Resolved
                    ? GraphConfidence.Heuristic
                    : confidence,
                ExtractorId = extractor, ExtractorVersion = version, Reason = reason,
            });
        }

        foreach (var file in files)
        {
            foreach (var type in file.Types)
            {
                var typeAnnotations = ReadAnnotations(file.Tokens, type.NameOffset);
                var classRoute = Mapping(typeAnnotations).FirstOrDefault().Path ?? string.Empty;
                foreach (var method in type.Methods)
                {
                    var annotations = ReadAnnotations(file.Tokens, method.OpenParen - 1);
                    ExtractSpringEndpoint(file, method, classRoute, annotations, resolveType, AddNode, AddEdge);
                    ExtractJavaTest(file, method, annotations, semanticEdges, AddNode, AddEdge);
                    ExtractJavaEntrypoint(file, method, annotations, AddNode, AddEdge);
                    ExtractJavaConfiguration(file, method, annotations, AddNode, AddEdge);
                }

                foreach (var annotation in typeAnnotations.Where(annotation => annotation.Name == "ConfigurationProperties"))
                {
                    var prefix = annotation.NamedArguments.GetValueOrDefault("prefix") ?? annotation.FirstString;
                    if (string.IsNullOrWhiteSpace(prefix)) continue;
                    var key = $"config:{prefix}";
                    AddNode(FrameworkNode(key, CodeNodeKind.ConfigurationKey, prefix!, prefix!, file, type.NameOffset,
                        "spring", "spring-configuration"));
                    AddEdge(type.Key, key, CodeEdgeKind.BindsConfiguration, "spring-configuration",
                        reason: "@ConfigurationProperties prefix");
                }
            }
        }
        return result;
    }

    private static void ExtractSpringEndpoint(
        JavaFile file,
        JavaMethod method,
        string classRoute,
        IReadOnlyList<JavaAnnotation> annotations,
        Func<string, JavaFile, string> resolveType,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, string, GraphConfidence, string?> addEdge)
    {
        foreach (var mapping in Mapping(annotations))
        {
            var route = NormalizeRoute(string.Join('/', new[] { classRoute, mapping.Path }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim('/'))));
            var endpointKey = $"endpoint:spring:{method.Key}";
            addNode(FrameworkNode(endpointKey, CodeNodeKind.Endpoint, method.Name, method.Key, file, method.OpenParen,
                "spring", "spring-web"));
            addEdge(endpointKey, method.Key, CodeEdgeKind.Handles, "spring-web", GraphConfidence.Resolved,
                "Spring mapping attached to parsed method declaration");
            var routeKey = $"route:{mapping.HttpMethod}:{route}:{endpointKey}";
            addNode(FrameworkNode(routeKey, CodeNodeKind.Route, $"{mapping.HttpMethod} {route}",
                $"{mapping.HttpMethod} {route}", file, method.OpenParen, "spring", "spring-web"));
            addEdge(routeKey, endpointKey, CodeEdgeKind.Handles, "spring-web", GraphConfidence.Exact,
                "Spring mapping route");

            foreach (var rawType in method.ParameterTypes)
            {
                var resolved = resolveType(rawType, file);
                if (string.IsNullOrWhiteSpace(resolved) || PrimitiveTypes.Contains(rawType)) continue;
                var contractKey = $"request-contract:{resolved}";
                addNode(FrameworkNode(contractKey, CodeNodeKind.RequestContract, StripTypeSyntax(rawType), resolved,
                    file, method.OpenParen, "spring", "spring-web"));
                addEdge(endpointKey, contractKey, CodeEdgeKind.Consumes, "spring-web", GraphConfidence.Resolved,
                    "Handler parameter type");
                addEdge(contractKey, resolved, CodeEdgeKind.References, "spring-web", GraphConfidence.Resolved,
                    "Structurally resolved request type");
            }
            var response = resolveType(method.ReturnType, file);
            if (!string.IsNullOrWhiteSpace(response) && !PrimitiveTypes.Contains(method.ReturnType))
            {
                var contractKey = $"response-contract:{response}";
                addNode(FrameworkNode(contractKey, CodeNodeKind.ResponseContract, StripTypeSyntax(method.ReturnType), response,
                    file, method.OpenParen, "spring", "spring-web"));
                addEdge(endpointKey, contractKey, CodeEdgeKind.Produces, "spring-web", GraphConfidence.Resolved,
                    "Handler return type");
                addEdge(contractKey, response, CodeEdgeKind.References, "spring-web", GraphConfidence.Resolved,
                    "Structurally resolved response type");
            }
        }
    }

    private static void ExtractJavaTest(
        JavaFile file,
        JavaMethod method,
        IReadOnlyCollection<JavaAnnotation> annotations,
        IReadOnlyCollection<CodeEdge> semanticEdges,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, string, GraphConfidence, string?> addEdge)
    {
        if (!annotations.Any(annotation => annotation.Name is "Test" or "ParameterizedTest" or "RepeatedTest" or "TestFactory"))
            return;
        var importedTest = file.Imports.GetValueOrDefault("Test") ?? string.Empty;
        var framework = importedTest.Contains("testng", StringComparison.OrdinalIgnoreCase) ? "testng" :
            importedTest.Contains("junit", StringComparison.OrdinalIgnoreCase) ? "junit" : "java-test";
        var testKey = $"test:{method.Key}";
        addNode(FrameworkNode(testKey, CodeNodeKind.Test, method.Name, method.Key, file, method.OpenParen,
            framework, "java-test"));
        addEdge(testKey, method.Key, CodeEdgeKind.Tests, "java-test", GraphConfidence.Exact,
            "Recognized test annotation");
        foreach (var call in semanticEdges.Where(edge => edge.Kind == CodeEdgeKind.Calls && edge.SourceKey == method.Key))
            addEdge(testKey, call.TargetKey, CodeEdgeKind.Covers, "java-test", GraphConfidence.Resolved,
                "Direct parsed call from test; this is not runtime coverage");
    }

    private static void ExtractJavaEntrypoint(
        JavaFile file,
        JavaMethod method,
        IReadOnlyCollection<JavaAnnotation> annotations,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, string, GraphConfidence, string?> addEdge)
    {
        var entry = annotations.FirstOrDefault(annotation => annotation.Name is
            "Scheduled" or "KafkaListener" or "RabbitListener" or "EventListener");
        if (entry is null) return;
        var isJob = entry.Name == "Scheduled";
        var kind = isJob ? CodeNodeKind.BackgroundJob : CodeNodeKind.EventConsumer;
        var technology = entry.Name switch
        {
            "KafkaListener" => "spring-kafka", "RabbitListener" => "spring-amqp",
            "EventListener" => "spring-events", _ => "spring-scheduling",
        };
        var key = $"{(isJob ? "job" : "consumer")}:{method.Key}";
        addNode(FrameworkNode(key, kind, method.Name, entry.FirstString ?? method.Key, file, method.OpenParen,
            technology, "spring-entrypoint"));
        addEdge(key, method.Key, CodeEdgeKind.Handles, "spring-entrypoint", GraphConfidence.Exact,
            $"@{entry.Name} entry point");
    }

    private static void ExtractJavaConfiguration(
        JavaFile file,
        JavaMethod method,
        IReadOnlyCollection<JavaAnnotation> annotations,
        Action<CodeNode> addNode,
        Action<string, string, CodeEdgeKind, string, GraphConfidence, string?> addEdge)
    {
        var keys = annotations.Where(annotation => annotation.Name == "Value")
            .Select(annotation => NormalizeSpringKey(annotation.FirstString)).OfType<string>().ToList();
        for (var index = method.BodyStart; index + 3 < method.BodyEnd && index >= 0; index++)
        {
            if (file.Tokens[index].Text is not ("getProperty" or "getRequiredProperty")) continue;
            var literal = file.Tokens.Skip(index + 1).Take(4).Select(token => StringLiteral(token.Text))
                .FirstOrDefault(value => value is not null);
            if (literal is not null) keys.Add(literal);
        }
        foreach (var keyValue in keys.Distinct(StringComparer.Ordinal))
        {
            var key = $"config:{keyValue}";
            addNode(FrameworkNode(key, CodeNodeKind.ConfigurationKey, keyValue, keyValue, file, method.OpenParen,
                "spring", "spring-configuration"));
            addEdge(method.Key, key, CodeEdgeKind.BindsConfiguration, "spring-configuration",
                GraphConfidence.Resolved, "Literal Spring configuration key read");
        }
    }

    private static IEnumerable<(string HttpMethod, string Path)> Mapping(IEnumerable<JavaAnnotation> annotations)
    {
        foreach (var annotation in annotations)
        {
            var method = annotation.Name switch
            {
                "GetMapping" => "GET", "PostMapping" => "POST", "PutMapping" => "PUT",
                "DeleteMapping" => "DELETE", "PatchMapping" => "PATCH",
                "RequestMapping" => annotation.NamedArguments.GetValueOrDefault("method")?.Split('.').Last() ?? "ANY",
                _ => null,
            };
            if (method is null) continue;
            var path = annotation.NamedArguments.GetValueOrDefault("path") ??
                       annotation.NamedArguments.GetValueOrDefault("value") ?? annotation.FirstString ?? string.Empty;
            yield return (method, path);
        }
    }

    private static List<JavaAnnotation> ReadAnnotations(IReadOnlyList<JavaToken> tokens, int declarationNameIndex)
    {
        var start = declarationNameIndex - 1;
        while (start >= 0 && tokens[start].Text is not (";" or "{" or "}")) start--;
        start++;
        var result = new List<JavaAnnotation>();
        for (var index = start; index < declarationNameIndex; index++)
        {
            if (tokens[index].Text != "@" || index + 1 >= declarationNameIndex) continue;
            var name = tokens[index + 1].Text;
            var firstString = default(string);
            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            if (index + 2 < declarationNameIndex && tokens[index + 2].Text == "(")
            {
                var close = FindMatching(tokens, index + 2, "(", ")");
                if (close > 0 && close < declarationNameIndex)
                {
                    for (var pos = index + 3; pos < close; pos++)
                    {
                        var literal = StringLiteral(tokens[pos].Text);
                        if (literal is not null && firstString is null) firstString = literal;
                        if (pos + 2 < close && IsIdentifier(tokens[pos].Text) && tokens[pos + 1].Text == "=")
                        {
                            var value = StringLiteral(tokens[pos + 2].Text) ??
                                        ReadQualifiedValue(tokens, pos + 2, close - 1);
                            named[tokens[pos].Text] = value;
                        }
                    }
                    index = close;
                }
            }
            result.Add(new JavaAnnotation(name, firstString, named));
        }
        return result;
    }

    private static string ReadQualifiedValue(IReadOnlyList<JavaToken> tokens, int start, int end)
    {
        var parts = new List<string>();
        for (var index = start; index <= end && tokens[index].Text is not ("," or ")"); index++)
            parts.Add(tokens[index].Text);
        return string.Concat(parts);
    }

    private static CodeNode FrameworkNode(string key, CodeNodeKind kind, string name, string? signature,
        JavaFile file, int tokenIndex, string technology, string extractor) => new()
    {
        Key = key, Kind = kind, Name = name, Signature = signature, FilePath = file.RelativePath,
        StartLine = LineOf(file.Content, file.Tokens[Math.Clamp(tokenIndex, 0, file.Tokens.Count - 1)].Offset),
        Language = "java", Technology = technology, SourceKind = GraphSourceKind.FrameworkAdapter,
        Confidence = GraphConfidence.Heuristic, ExtractorId = extractor, ExtractorVersion = "1.0.0",
        Reason = "Framework annotation matched structurally; classpath symbol identity was not available.",
    };

    private static string NormalizeRoute(string route) => "/" + route.Trim().Trim('/');

    private static string? NormalizeSpringKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
            value = value[2..^1].Split(':')[0];
        return value;
    }

    private static string? StringLiteral(string token) => token.Length >= 2 &&
        ((token[0] == '"' && token[^1] == '"') || (token[0] == '\'' && token[^1] == '\''))
        ? token[1..^1] : null;

    private static string? ResolveReceiver(
        IReadOnlyList<JavaToken> tokens,
        int methodNameIndex,
        JavaType currentType,
        IReadOnlyDictionary<string, string> variables,
        Func<string, JavaFile, string> resolveType,
        JavaFile file)
    {
        if (methodNameIndex < 2 || tokens[methodNameIndex - 1].Text != ".")
            return null;
        var receiver = tokens[methodNameIndex - 2].Text;
        if (receiver == "this")
            return currentType.Key;
        if (receiver == "super")
            return currentType.Extends;
        return variables.TryGetValue(receiver, out var variableType) ? variableType : resolveType(receiver, file);
    }

    private static void AddLocalVariables(
        IReadOnlyList<JavaToken> tokens,
        int start,
        int end,
        Func<string, JavaFile, string> resolveType,
        JavaFile file,
        IDictionary<string, string> variables)
    {
        for (var index = start; index + 2 <= end; index++)
        {
            if (!IsIdentifier(tokens[index].Text) || !IsIdentifier(tokens[index + 1].Text) ||
                tokens[index + 2].Text is not ("=" or ";" or ","))
                continue;
            if (Keywords.Contains(tokens[index].Text) && tokens[index].Text != "var")
                continue;
            var resolved = resolveType(tokens[index].Text, file);
            if (!string.IsNullOrWhiteSpace(resolved))
                variables[tokens[index + 1].Text] = resolved;
        }
    }

    private static bool IsPlausibleDeclaration(IReadOnlyList<JavaToken> tokens, JavaType type, int nameIndex, int openParen)
    {
        if (tokens[nameIndex].Text == type.Name)
            return true; // constructor
        if (nameIndex == 0 || tokens[nameIndex - 1].Text is "." or "::")
            return false;
        var prefix = tokens[nameIndex - 1].Text;
        return IsIdentifier(prefix) || prefix is ">" or "]";
    }

    private static int FindMethodBodyStart(IReadOnlyList<JavaToken> tokens, int start, int limit)
    {
        for (var index = start; index < limit; index++)
        {
            if (tokens[index].Text is "{" or ";")
                return index;
            if (tokens[index].Text == "=")
                return -1;
        }
        return -1;
    }

    private static List<string> ParseParameterTypes(IReadOnlyList<JavaToken> tokens, int start, int end)
    {
        if (end < start)
            return [];
        var parameters = SplitOnTopLevel(tokens, start, end, ",");
        return parameters.Where(parameter => parameter.Count > 0)
            .Select(parameter => FirstTypeName(parameter.Take(Math.Max(0, parameter.Count - 1)).ToList()))
            .Select(StripTypeSyntax)
            .ToList();
    }

    private static string FindReturnType(IReadOnlyList<JavaToken> tokens, JavaType type, int methodNameIndex)
    {
        for (var index = methodNameIndex - 1; index > type.OpenBrace; index--)
        {
            var token = tokens[index].Text;
            if (token is ";" or "{" or "}") break;
            if (IsIdentifier(token) && token is not
                ("public" or "protected" or "private" or "static" or "final" or "abstract" or "synchronized" or "native" or "default"))
                return token;
        }
        return string.Empty;
    }

    private static List<string> ParseVariableTypes(IReadOnlyList<JavaToken> tokens, int start, int end)
    {
        if (end < start)
            return [];
        return SplitOnTopLevel(tokens, start, end, ",")
            .Select(parameter => parameter.LastOrDefault(token => IsIdentifier(token.Text) && !Keywords.Contains(token.Text))?.Text)
            .OfType<string>()
            .ToList();
    }

    private static int CountArguments(IReadOnlyList<JavaToken> tokens, int start, int end) =>
        end < start ? 0 : SplitOnTopLevel(tokens, start, end, ",").Count(parameter => parameter.Count > 0);

    private static List<List<JavaToken>> SplitOnTopLevel(IReadOnlyList<JavaToken> tokens, int start, int end, string separator)
    {
        var result = new List<List<JavaToken>>();
        var current = new List<JavaToken>();
        var depth = 0;
        for (var index = start; index <= end && index < tokens.Count; index++)
        {
            var token = tokens[index].Text;
            if (token is "(" or "[" or "<") depth++;
            if (token == separator && depth == 0) { result.Add(current); current = []; continue; }
            current.Add(tokens[index]);
            if (token is ")" or "]" or ">") depth = Math.Max(0, depth - 1);
        }
        result.Add(current);
        return result;
    }

    private static string FirstTypeName(IReadOnlyList<JavaToken> tokens)
    {
        foreach (var token in tokens)
        {
            if (IsIdentifier(token.Text) && !Keywords.Contains(token.Text))
                return token.Text;
        }
        return string.Empty;
    }

    private static int FindMatching(IReadOnlyList<JavaToken> tokens, int open, string opening, string closing)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Text == opening) depth++;
            else if (tokens[index].Text == closing && --depth == 0) return index;
        }
        return -1;
    }

    private static bool IsIdentifier(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] is '_' or '$');

    private static string StripTypeSyntax(string value)
    {
        var end = value.IndexOf('<');
        value = end >= 0 ? value[..end] : value;
        return value.TrimEnd('[', ']', '.', '.', '.');
    }

    private static int LineOf(string content, int offset) => 1 + content.Take(Math.Clamp(offset, 0, content.Length)).Count(character => character == '\n');

    private sealed class JavaFile(string relativePath, string content, List<JavaToken> tokens, string packageName, Dictionary<string, string> imports)
    {
        public string RelativePath { get; } = relativePath;
        public string Content { get; } = content;
        public List<JavaToken> Tokens { get; } = tokens;
        public string Package { get; } = packageName;
        public Dictionary<string, string> Imports { get; } = imports;
        public List<JavaType> Types { get; } = [];

        public static JavaFile Parse(string relativePath, string content)
        {
            var tokens = JavaLexer.Lex(content);
            var packageName = ReadQualifiedDirective(tokens, "package") ?? string.Empty;
            var imports = ReadImports(tokens);
            var file = new JavaFile(relativePath, content, tokens, packageName, imports);
            ParseTypes(file);
            return file;
        }

        private static Dictionary<string, string> ReadImports(IReadOnlyList<JavaToken> tokens)
        {
            var imports = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (tokens[index].Text != "import") continue;
                var end = FindSemicolon(tokens, index + 1);
                if (end < 0) continue;
                var start = index + 1;
                if (start < end && tokens[start].Text == "static") start++;
                var qualified = JoinQualified(tokens, start, end - 1);
                if (!string.IsNullOrWhiteSpace(qualified) && !qualified.EndsWith(".*", StringComparison.Ordinal))
                    imports[qualified[(qualified.LastIndexOf('.') + 1)..]] = qualified;
                index = end;
            }
            return imports;
        }

        private static string? ReadQualifiedDirective(IReadOnlyList<JavaToken> tokens, string directive)
        {
            var index = tokens.Select((token, position) => (token, position)).FirstOrDefault(item => item.token.Text == directive).position;
            if (index < 0 || index >= tokens.Count || tokens[index].Text != directive) return null;
            var end = FindSemicolon(tokens, index + 1);
            return end < 0 ? null : JoinQualified(tokens, index + 1, end - 1);
        }

        private static int FindSemicolon(IReadOnlyList<JavaToken> tokens, int start)
        {
            for (var index = start; index < tokens.Count; index++) if (tokens[index].Text == ";") return index;
            return -1;
        }

        private static string JoinQualified(IReadOnlyList<JavaToken> tokens, int start, int end) =>
            string.Concat(tokens.Skip(start).Take(Math.Max(0, end - start + 1)).Select(token => token.Text));

        private static void ParseTypes(JavaFile file)
        {
            for (var index = 0; index < file.Tokens.Count - 2; index++)
            {
                if (file.Tokens[index].Text is not ("class" or "interface" or "enum" or "record") ||
                    !IsIdentifier(file.Tokens[index + 1].Text))
                    continue;

                var openBrace = index + 2;
                while (openBrace < file.Tokens.Count && file.Tokens[openBrace].Text != "{") openBrace++;
                if (openBrace >= file.Tokens.Count) continue;
                var closeBrace = FindMatching(file.Tokens, openBrace, "{", "}");
                if (closeBrace < 0) continue;
                var parent = file.Types.Where(type => index > type.OpenBrace && index < type.CloseBrace)
                    .OrderByDescending(type => type.OpenBrace).FirstOrDefault();
                var name = file.Tokens[index + 1].Text;
                var key = parent is null
                    ? (string.IsNullOrWhiteSpace(file.Package) ? name : $"{file.Package}.{name}")
                    : $"{parent.Key}.{name}";
                var type = new JavaType(name, key, file.Package, index + 1, openBrace, closeBrace, parent);

                ParseTypeRelationships(file.Tokens, index + 2, openBrace - 1, type);
                file.Types.Add(type);
            }
        }

        private static void ParseTypeRelationships(IReadOnlyList<JavaToken> tokens, int start, int end, JavaType type)
        {
            for (var index = start; index <= end; index++)
            {
                if (tokens[index].Text == "extends")
                {
                    var next = ReadTypeReference(tokens, index + 1, end);
                    type.Extends = next.Name;
                    index = next.End;
                }
                else if (tokens[index].Text == "implements")
                {
                    for (var position = index + 1; position <= end;)
                    {
                        var next = ReadTypeReference(tokens, position, end);
                        if (string.IsNullOrWhiteSpace(next.Name)) break;
                        type.Implements.Add(next.Name);
                        position = next.End + 1;
                        if (position > end || tokens[position].Text != ",") break;
                        position++;
                    }
                    break;
                }
            }
        }

        private static (string Name, int End) ReadTypeReference(IReadOnlyList<JavaToken> tokens, int start, int end)
        {
            var parts = new List<string>();
            var angleDepth = 0;
            for (var index = start; index <= end; index++)
            {
                if (tokens[index].Text == "<") { angleDepth++; continue; }
                if (tokens[index].Text == ">") { angleDepth--; continue; }
                if (angleDepth > 0) continue;
                if (tokens[index].Text == "," || tokens[index].Text is "implements" or "extends") return (string.Concat(parts), index - 1);
                if (tokens[index].Text is "{" or ")") return (string.Concat(parts), index - 1);
                parts.Add(tokens[index].Text);
            }
            return (string.Concat(parts), end);
        }
    }

    private sealed class JavaType(string name, string key, string packageName, int nameOffset, int openBrace, int closeBrace, JavaType? parent)
    {
        public string Name { get; } = name;
        public string Key { get; } = key;
        public string Package { get; } = packageName;
        public int NameOffset { get; } = nameOffset;
        public int OpenBrace { get; } = openBrace;
        public int CloseBrace { get; } = closeBrace;
        public JavaType? Parent { get; } = parent;
        public string? Extends { get; set; }
        public List<string> Implements { get; } = [];
        public List<JavaMethod> Methods { get; } = [];
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);
    }

    private sealed record JavaMethod(
        string Key,
        string Name,
        List<string> ParameterTypes,
        List<string> BindingParameterTypes,
        string ReturnType,
        int OpenParen, int BodyStart, int BodyEnd, List<string> Parameters);
    private sealed record JavaAnnotation(string Name, string? FirstString, Dictionary<string, string> NamedArguments);
    private sealed record JavaToken(string Text, int Offset);

    private static class JavaLexer
    {
        public static List<JavaToken> Lex(string source)
        {
            var tokens = new List<JavaToken>();
            for (var index = 0; index < source.Length;)
            {
                var current = source[index];
                if (char.IsWhiteSpace(current)) { index++; continue; }
                if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
                {
                    index = source.IndexOf('\n', index + 2); if (index < 0) break; continue;
                }
                if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
                {
                    var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    index = end < 0 ? source.Length : end + 2; continue;
                }
                if (current is '\'' or '"')
                {
                    var quote = current;
                    var start = index++;
                    while (index < source.Length)
                    {
                        if (source[index++] == '\\' && index < source.Length) { index++; continue; }
                        if (source[index - 1] == quote) break;
                    }
                    // Preserve literal text for framework metadata such as route and
                    // configuration keys. It remains one token, so braces/parentheses
                    // inside strings cannot corrupt structural parsing.
                    tokens.Add(new JavaToken(source[start..index], start));
                    continue;
                }
                if (char.IsLetter(current) || current is '_' or '$')
                {
                    var start = index++;
                    while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] is '_' or '$')) index++;
                    tokens.Add(new JavaToken(source[start..index], start));
                    continue;
                }
                if (char.IsDigit(current))
                {
                    var start = index++;
                    while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] is '.' or '_')) index++;
                    tokens.Add(new JavaToken("<number>", start));
                    continue;
                }
                if (current == ':' && index + 1 < source.Length && source[index + 1] == ':')
                {
                    tokens.Add(new JavaToken("::", index)); index += 2; continue;
                }
                if (current == '.' && index + 2 < source.Length && source[index + 1] == '.' && source[index + 2] == '.')
                {
                    tokens.Add(new JavaToken("...", index)); index += 3; continue;
                }
                tokens.Add(new JavaToken(current.ToString(), index++));
            }
            return tokens;
        }
    }
}
