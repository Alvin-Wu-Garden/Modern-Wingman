using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

/// <summary>Common EF Core/JPA mapping adapter. Convention-only matches are explicitly Heuristic, never Exact.</summary>
public sealed partial class OrmDataArtifactAdapter : IDataArtifactAdapter
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> CachedPlatformReferences =
        new(CreatePlatformReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    public string Id => "wingman.orm-mapping";
    public string Version => "1.0.0";
    public bool CanAnalyze(DataArtifact artifact) => Path.GetExtension(artifact.FilePath).ToLowerInvariant() switch
    {
        ".cs" => CSharpDataSignalRegex().IsMatch(artifact.Content),
        ".java" => JavaDataSignalRegex().IsMatch(artifact.Content),
        _ => false,
    };

    public DataExtractionResult Analyze(DataArtifact artifact)
    {
        var graph = new DataGraphBuilder(Id, Version, artifact.RelativePath, artifact.ContentHash);
        var diagnostics = new List<DataExtractionDiagnostic>();
        var source = SourceLinkResolver.Create(artifact);
        graph.EnsureFile(source.Language, GraphSourceKind.Ast, GraphConfidence.Resolved);
        if (Path.GetExtension(artifact.FilePath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            ExtractCSharpMappings(artifact, graph, source);
            ExtractEfMigrations(artifact, graph);
            ExtractEmbeddedQueries(artifact, graph, CSharpQueryRegex(), source);
        }
        else
        {
            ExtractJavaMappings(artifact, graph, source);
            ExtractEmbeddedQueries(artifact, graph, JavaQueryRegex(), source);
        }
        return new(graph.Result, diagnostics, []);
    }

    private static void ExtractCSharpMappings(DataArtifact artifact, DataGraphBuilder graph, SourceLinkResolver source)
    {
        foreach (Match match in CSharpTableAttributeRegex().Matches(artifact.Content))
            AddEntityMapping(graph, source, match.Groups["type"].Value, match.Groups["table"].Value, match.Groups["schema"].Value, LineAt(artifact.Content, match.Index), match.Index, "EF Core attribute");
        foreach (Match match in EfFluentTableRegex().Matches(artifact.Content))
            AddEntityMapping(graph, source, match.Groups["type"].Value, match.Groups["table"].Value, match.Groups["schema"].Value, LineAt(artifact.Content, match.Index), match.Index, "EF Core fluent mapping");
    }

    private static void ExtractJavaMappings(DataArtifact artifact, DataGraphBuilder graph, SourceLinkResolver source)
    {
        foreach (Match match in JpaTableRegex().Matches(artifact.Content))
            AddEntityMapping(graph, source, match.Groups["type"].Value, match.Groups["table"].Value, match.Groups["schema"].Value, LineAt(artifact.Content, match.Index), match.Index, "JPA @Table mapping");
    }

    private static void AddEntityMapping(DataGraphBuilder graph, SourceLinkResolver source,
        string typeName, string tableName, string schema, int line, int sourceOffset, string reason)
    {
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(typeName)) return;
        var identifier = string.IsNullOrWhiteSpace(schema) ? tableName : $"{schema}.{tableName}";
        var table = graph.EnsureTable(identifier, GraphSourceKind.Heuristic, GraphConfidence.Heuristic);
        var typeKey = source.ResolveType(typeName, sourceOffset);
        if (typeKey is null)
        {
            graph.AddEdge(source.FileKey, table, CodeEdgeKind.MapsTo, GraphSourceKind.Heuristic,
                GraphConfidence.Heuristic, $"{reason}; type binding was unavailable, so the source file is retained as provenance.");
            return;
        }

        graph.AddNode(typeKey, CodeNodeKind.Type, typeName.Split('.').Last(), source.Language,
            GraphSourceKind.Ast, GraphConfidence.Resolved, line, signature: typeKey, technology: reason,
            reason: "Canonical type key aligned with the language code analyzer.");
        graph.AddEdge(typeKey, table, CodeEdgeKind.MapsTo, GraphSourceKind.Heuristic,
            GraphConfidence.Heuristic, reason);

        // Framework extractors only create these contract nodes when the mapped type is
        // actually used by an endpoint. ProjectIndexService removes dangling edges, so
        // these candidates neither invent API contracts nor lose a valid contract→data path.
        graph.AddEdge($"request-contract:{typeKey}", table, CodeEdgeKind.SerializesTo,
            GraphSourceKind.Heuristic, GraphConfidence.Heuristic, $"API request contract uses ORM-mapped type {typeKey}.");
        graph.AddEdge($"response-contract:{typeKey}", table, CodeEdgeKind.SerializesTo,
            GraphSourceKind.Heuristic, GraphConfidence.Heuristic, $"API response contract uses ORM-mapped type {typeKey}.");
    }

    private static void ExtractEfMigrations(DataArtifact artifact, DataGraphBuilder graph)
    {
        if (!artifact.Content.Contains("Migration", StringComparison.Ordinal) && !artifact.Content.Contains("migrationBuilder", StringComparison.Ordinal)) return;
        var migration = graph.AddNode($"migration:{artifact.RelativePath.ToLowerInvariant()}", CodeNodeKind.Migration, Path.GetFileNameWithoutExtension(artifact.RelativePath), "csharp", GraphSourceKind.Migration, GraphConfidence.Resolved, 1, technology: "efcore");
        foreach (Match match in EfCreateTableRegex().Matches(artifact.Content))
        {
            var table = graph.EnsureTable(Qualify(match.Groups["schema"].Value, match.Groups["table"].Value), GraphSourceKind.Migration, GraphConfidence.Resolved);
            graph.AddEdge(migration, table, CodeEdgeKind.Migrates, GraphSourceKind.Migration, GraphConfidence.Resolved, "EF Core migrationBuilder.CreateTable");
        }
        foreach (Match match in EfColumnRegex().Matches(artifact.Content))
        {
            var table = graph.EnsureTable(Qualify(match.Groups["schema"].Value, match.Groups["table"].Value), GraphSourceKind.Migration, GraphConfidence.Resolved);
            var columnName = match.Groups["column"].Value;
            var column = graph.AddNode($"column:{table[6..]}.{DataGraphBuilder.Normalize(columnName)}", CodeNodeKind.Column, columnName, "csharp", GraphSourceKind.Migration, GraphConfidence.Resolved, LineAt(artifact.Content, match.Index), technology: "efcore");
            graph.AddEdge(table, column, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Resolved);
            graph.AddEdge(migration, column, CodeEdgeKind.Migrates, GraphSourceKind.Migration, GraphConfidence.Resolved);
        }
    }

    private static void ExtractEmbeddedQueries(DataArtifact artifact, DataGraphBuilder graph, Regex queryRegex,
        SourceLinkResolver source)
    {
        foreach (Match match in queryRegex.Matches(artifact.Content))
        {
            var sql = Regex.Unescape(match.Groups["sql"].Value);
            var line = LineAt(artifact.Content, match.Index);
            var query = graph.AddNode($"query:{artifact.RelativePath.ToLowerInvariant()}:{line}", CodeNodeKind.Query,
                $"Embedded query at line {line}", source.Language, GraphSourceKind.Sql,
                GraphConfidence.Resolved, line, signature: sql.Length > 500 ? sql[..500] : sql,
                reason: "SQL literal extracted from source code.");
            graph.AddEdge(source.FileKey, query, CodeEdgeKind.Contains, GraphSourceKind.Ast,
                GraphConfidence.Resolved, "Embedded query declared in source file.");
            var callable = source.ResolveContainingCallable(match.Index);
            if (callable is not null)
                graph.AddEdge(callable, query, CodeEdgeKind.Contains, GraphSourceKind.Ast,
                    GraphConfidence.Resolved, "Embedded query declared inside callable.");

            var reads = EmbeddedReadRegex().Matches(sql).Select(item => item.Groups["table"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var writes = EmbeddedWriteRegex().Matches(sql).Select(item => item.Groups["table"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var identifier in reads)
                graph.AddEdge(query, graph.EnsureTable(identifier), CodeEdgeKind.Reads,
                    GraphSourceKind.Sql, GraphConfidence.Resolved, "SQL FROM/JOIN target.");
            foreach (var identifier in writes)
                graph.AddEdge(query, graph.EnsureTable(identifier), CodeEdgeKind.Writes,
                    GraphSourceKind.Sql, GraphConfidence.Resolved, "SQL INSERT/UPDATE/DELETE target.");
            AddEmbeddedColumnEdges(sql, query, reads, writes, graph, line);
        }
    }

    private static void AddEmbeddedColumnEdges(string sql, string query, IReadOnlyList<string> reads,
        IReadOnlyList<string> writes, DataGraphBuilder graph, int line)
    {
        if (reads.Count == 1)
        {
            var table = graph.EnsureTable(reads[0]);
            foreach (var column in SelectColumns(sql))
                graph.AddEdge(query, graph.EnsureColumn(table, column, line: line,
                        reason: "Column inferred from a single-table SELECT list."), CodeEdgeKind.Reads,
                    GraphSourceKind.Sql, GraphConfidence.Resolved, "Single-table SELECT column.");
        }
        if (writes.Count == 1)
        {
            var table = graph.EnsureTable(writes[0]);
            foreach (var column in WriteColumns(sql))
                graph.AddEdge(query, graph.EnsureColumn(table, column, line: line,
                        reason: "Column inferred from INSERT/UPDATE syntax."), CodeEdgeKind.Writes,
                    GraphSourceKind.Sql, GraphConfidence.Resolved, "Write target column.");
        }
    }

    private static IEnumerable<string> SelectColumns(string sql)
    {
        var match = SelectListRegex().Match(sql);
        if (!match.Success) yield break;
        foreach (var item in match.Groups["columns"].Value.Split(','))
        {
            var value = item.Trim();
            if (value == "*" || value.Contains('(')) continue;
            value = Regex.Replace(value, @"\s+AS\s+.+$", "", RegexOptions.IgnoreCase).Trim();
            value = value.Split('.').Last().Trim('[', ']', '`', '"');
            if (SimpleIdentifierRegex().IsMatch(value)) yield return value;
        }
    }

    private static IEnumerable<string> WriteColumns(string sql)
    {
        var insert = InsertColumnsRegex().Match(sql);
        if (insert.Success)
            foreach (var value in insert.Groups["columns"].Value.Split(','))
                if (SimpleIdentifierRegex().IsMatch(value.Trim())) yield return value.Trim().Trim('[', ']', '`', '"');
        foreach (Match update in UpdateColumnRegex().Matches(sql))
            yield return update.Groups["column"].Value.Trim('[', ']', '`', '"');
    }

    private static string Qualify(string schema, string table) => string.IsNullOrWhiteSpace(schema) ? table : $"{schema}.{table}";
    private static int LineAt(string content, int index)
    {
        var count = 1;
        for (var i = 0; i < Math.Clamp(index, 0, content.Length); i++) if (content[i] == '\n') count++;
        return count;
    }

    [GeneratedRegex("""\[Table\(\s*"(?<table>[^"]+)"(?:\s*,\s*Schema\s*=\s*"(?<schema>[^"]+)")?\s*\)\][\s\S]{0,300}?\bclass\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase)] private static partial Regex CSharpTableAttributeRegex();
    [GeneratedRegex("""Entity\s*<\s*(?<type>[A-Za-z_][A-Za-z0-9_.]*)\s*>\s*\([^)]*\)[\s\S]{0,500}?ToTable\(\s*"(?<table>[^"]+)"(?:\s*,\s*"(?<schema>[^"]+)")?""", RegexOptions.IgnoreCase)] private static partial Regex EfFluentTableRegex();
    [GeneratedRegex("""@Table\s*\(\s*(?:name\s*=\s*)?"(?<table>[^"]+)"(?:\s*,\s*schema\s*=\s*"(?<schema>[^"]+)")?[^)]*\)[\s\S]{0,300}?\bclass\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.IgnoreCase)] private static partial Regex JpaTableRegex();
    [GeneratedRegex("""CreateTable\s*\(\s*name\s*:\s*"(?<table>[^"]+)"(?:\s*,\s*schema\s*:\s*"(?<schema>[^"]+)")?""", RegexOptions.IgnoreCase)] private static partial Regex EfCreateTableRegex();
    [GeneratedRegex("""AddColumn\s*<[^>]+>\s*\(\s*name\s*:\s*"(?<column>[^"]+)"\s*,\s*(?:schema\s*:\s*"(?<schema>[^"]+)"\s*,\s*)?table\s*:\s*"(?<table>[^"]+)["]""", RegexOptions.IgnoreCase)] private static partial Regex EfColumnRegex();
    [GeneratedRegex("""(?:FromSqlRaw|FromSqlInterpolated|ExecuteSqlRaw|ExecuteSqlInterpolated)\s*\(\s*(?:\$|@|\$@|@\$)?"(?<sql>(?:\\.|[^"])*)["]""", RegexOptions.IgnoreCase)] private static partial Regex CSharpQueryRegex();
    [GeneratedRegex("""(?:query|queryForObject|update|execute)\s*\(\s*"(?<sql>(?:\\.|[^"])*)["]""", RegexOptions.IgnoreCase)] private static partial Regex JavaQueryRegex();
    [GeneratedRegex("""\b(?:FROM|JOIN)\s+(?<table>[A-Za-z_][A-Za-z0-9_.$]*(?:\.[A-Za-z_][A-Za-z0-9_$]*)?)""", RegexOptions.IgnoreCase)] private static partial Regex EmbeddedReadRegex();
    [GeneratedRegex("""\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+(?<table>[A-Za-z_][A-Za-z0-9_.$]*(?:\.[A-Za-z_][A-Za-z0-9_$]*)?)""", RegexOptions.IgnoreCase)] private static partial Regex EmbeddedWriteRegex();
    [GeneratedRegex(@"\bSELECT\s+(?<columns>.+?)\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex SelectListRegex();
    [GeneratedRegex(@"\bINSERT\s+INTO\s+[^\s(]+\s*\((?<columns>[^)]+)\)", RegexOptions.IgnoreCase)] private static partial Regex InsertColumnsRegex();
    [GeneratedRegex(@"(?:\bSET\b|,)\s*(?<column>[A-Za-z_][A-Za-z0-9_$]*)\s*=", RegexOptions.IgnoreCase)] private static partial Regex UpdateColumnRegex();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$]*$")] private static partial Regex SimpleIdentifierRegex();

    private sealed class SourceLinkResolver
    {
        private readonly DataArtifact _artifact;
        private readonly SyntaxNode? _csharpRoot;
        private readonly SemanticModel? _csharpModel;
        private readonly string _javaPackage;

        private SourceLinkResolver(DataArtifact artifact, SyntaxNode? csharpRoot, SemanticModel? csharpModel,
            string javaPackage)
        {
            _artifact = artifact;
            _csharpRoot = csharpRoot;
            _csharpModel = csharpModel;
            _javaPackage = javaPackage;
        }

        public string Language => Path.GetExtension(_artifact.FilePath).Equals(".java", StringComparison.OrdinalIgnoreCase)
            ? "java" : "csharp";
        public string FileKey => $"file:{_artifact.RelativePath}";

        public static SourceLinkResolver Create(DataArtifact artifact)
        {
            if (Path.GetExtension(artifact.FilePath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var tree = CSharpSyntaxTree.ParseText(artifact.Content, path: artifact.FilePath);
                var compilation = CSharpCompilation.Create("WingmanDataMapping", [tree], CachedPlatformReferences.Value,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                return new(artifact, tree.GetRoot(), compilation.GetSemanticModel(tree), string.Empty);
            }
            var package = JavaPackageRegex().Match(artifact.Content).Groups["package"].Value;
            return new(artifact, null, null, package);
        }

        public string? ResolveType(string rawName, int offset)
        {
            if (_csharpRoot is not null && _csharpModel is not null)
            {
                var simpleName = rawName.Split('.').Last();
                var declarations = _csharpRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                    .Where(item => item.Identifier.ValueText == simpleName).ToList();
                var declaration = declarations.Count == 1 ? declarations[0] : declarations.FirstOrDefault(item => item.Span.Contains(offset));
                return declaration is null || _csharpModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol
                    ? null : RoslynCodeAnalyzer.SymbolKey(symbol);
            }
            var name = rawName.Trim();
            if (name.Length == 0) return null;
            if (name.Contains('.')) return name;
            var javaDeclarations = JavaTypeRegex().Matches(_artifact.Content)
                .Where(match => string.Equals(match.Groups["name"].Value, name, StringComparison.Ordinal))
                .ToList();
            var javaDeclaration = javaDeclarations.Count == 1
                ? javaDeclarations[0]
                : javaDeclarations.FirstOrDefault(match => match.Index >= offset && match.Index - offset <= 500);
            return javaDeclaration is null ? null : JavaTypeKey(javaDeclaration);
        }

        public string? ResolveContainingCallable(int offset)
        {
            if (_csharpRoot is not null && _csharpModel is not null)
            {
                var token = _csharpRoot.FindToken(Math.Clamp(offset, 0, Math.Max(0, _artifact.Content.Length - 1)));
                var declaration = token.Parent?.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
                return declaration is null || _csharpModel.GetDeclaredSymbol(declaration) is not IMethodSymbol symbol
                    ? null : RoslynCodeAnalyzer.SymbolKey(symbol);
            }
            return ResolveJavaCallable(offset);
        }

        private string? ResolveJavaCallable(int offset)
        {
            foreach (Match method in JavaMethodRegex().Matches(_artifact.Content))
            {
                var open = _artifact.Content.IndexOf('{', method.Index + method.Length - 1);
                if (open < 0 || offset < open) continue;
                var close = FindBraceClose(_artifact.Content, open);
                if (close < offset) continue;
                var typeName = JavaEnclosingType(open);
                if (typeName is null) return null;
                var parameters = method.Groups["parameters"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(parameter => parameter.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
                    .Select(type => type.Split('<')[0].Trim()).Where(type => type.Length > 0);
                return $"{typeName}.{method.Groups["name"].Value}({string.Join(',', parameters)})";
            }
            return null;
        }

        private string? JavaEnclosingType(int offset)
        {
            Match? selected = null;
            foreach (Match type in JavaTypeRegex().Matches(_artifact.Content))
            {
                if (type.Index >= offset) break;
                var open = _artifact.Content.IndexOf('{', type.Index + type.Length - 1);
                if (open >= 0 && FindBraceClose(_artifact.Content, open) >= offset) selected = type;
            }
            if (selected is null) return null;
            return JavaTypeKey(selected);
        }

        private string JavaTypeKey(Match declaration)
        {
            var names = new List<string>();
            foreach (Match candidate in JavaTypeRegex().Matches(_artifact.Content))
            {
                if (candidate.Index > declaration.Index) break;
                var open = _artifact.Content.IndexOf('{', candidate.Index + candidate.Length - 1);
                if (candidate == declaration || open >= 0 && FindBraceClose(_artifact.Content, open) >= declaration.Index)
                    names.Add(candidate.Groups["name"].Value);
            }
            var typeName = string.Join('.', names);
            return string.IsNullOrWhiteSpace(_javaPackage) ? typeName : $"{_javaPackage}.{typeName}";
        }

        private static int FindBraceClose(string value, int open)
        {
            var depth = 0;
            for (var index = open; index < value.Length; index++)
            {
                if (value[index] == '{') depth++;
                else if (value[index] == '}' && --depth == 0) return index;
            }
            return -1;
        }

    }

    private static IReadOnlyList<MetadataReference> CreatePlatformReferences()
    {
        var paths = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        return string.IsNullOrWhiteSpace(paths)
            ? [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]
            : paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }

    [GeneratedRegex(@"(?:\[\s*Table\s*\(|\.ToTable\s*\(|\bmigrationBuilder\b|\b(?:FromSqlRaw|FromSqlInterpolated|ExecuteSqlRaw|ExecuteSqlInterpolated)\s*\()", RegexOptions.IgnoreCase)] private static partial Regex CSharpDataSignalRegex();
    [GeneratedRegex(@"(?:@Table\s*\(|\b(?:query|queryForObject|update|execute)\s*\()", RegexOptions.IgnoreCase)] private static partial Regex JavaDataSignalRegex();
    [GeneratedRegex(@"\bpackage\s+(?<package>[A-Za-z_][A-Za-z0-9_.]*)\s*;")] private static partial Regex JavaPackageRegex();
    [GeneratedRegex(@"\b(?:class|record|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)[^;{]*\{")] private static partial Regex JavaTypeRegex();
    [GeneratedRegex(@"(?:public|protected|private|static|final|synchronized|abstract|native|\s)+[A-Za-z_][A-Za-z0-9_<>.?\[\]]*\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<parameters>[^)]*)\)\s*(?:throws\s+[^\{]+)?\{")] private static partial Regex JavaMethodRegex();
}
