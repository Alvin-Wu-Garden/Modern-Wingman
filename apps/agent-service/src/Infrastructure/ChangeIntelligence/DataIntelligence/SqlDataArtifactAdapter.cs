using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

/// <summary>Dialect-tolerant DDL/query extractor. It accepts common ANSI/SQL Server/PostgreSQL/MySQL syntax and marks unresolved syntax as a capability gap.</summary>
public sealed partial class SqlDataArtifactAdapter : IDataArtifactAdapter
{
    public string Id => "wingman.sql-schema";
    public string Version => "1.0.0";
    public bool CanAnalyze(DataArtifact artifact) => Path.GetExtension(artifact.FilePath).Equals(".sql", StringComparison.OrdinalIgnoreCase);

    public DataExtractionResult Analyze(DataArtifact artifact)
    {
        var builder = new DataGraphBuilder(Id, Version, artifact.RelativePath, artifact.ContentHash);
        var diagnostics = new List<DataExtractionDiagnostic>();
        var gaps = new List<string>();
        var statements = SplitStatements(RemoveComments(artifact.Content));
        var fileKey = builder.EnsureFile("sql", GraphSourceKind.Sql, GraphConfidence.Exact);
        var hasMigrationStatements = statements.Any(statement => IsMigrationStatement(statement.Text));
        var migrationKey = hasMigrationStatements
            ? builder.AddNode($"migration:{artifact.RelativePath.ToLowerInvariant()}", CodeNodeKind.Migration,
                Path.GetFileNameWithoutExtension(artifact.RelativePath), "sql", GraphSourceKind.Migration,
                GraphConfidence.Exact, line: 1, technology: "sql-migration",
                reason: "File contains parsed DDL statements.")
            : null;
        if (migrationKey is not null)
            builder.AddEdge(fileKey, migrationKey, CodeEdgeKind.Contains, GraphSourceKind.Migration,
                GraphConfidence.Exact, "SQL file declares migration DDL.");

        var index = 0;
        foreach (var statement in statements)
        {
            index++;
            if (migrationKey is not null && TryExtractCreateTable(statement.Text, statement.Line, builder, migrationKey)) continue;
            if (migrationKey is not null && TryExtractCreateView(statement.Text, statement.Line, builder, migrationKey)) continue;
            if (migrationKey is not null && TryExtractCreateIndex(statement.Text, statement.Line, builder, migrationKey)) continue;
            if (migrationKey is not null && TryExtractProcedure(statement.Text, statement.Line, builder, migrationKey)) continue;
            if (migrationKey is not null && TryExtractAlterTable(statement.Text, statement.Line, builder, migrationKey)) continue;
            if (TryExtractQuery(statement.Text, statement.Line, index, builder, fileKey, artifact.RelativePath)) continue;

            if (ContainsDataKeyword(statement.Text))
                diagnostics.Add(new(artifact.RelativePath, Id, "warning", $"第 {statement.Line} 行的 SQL artifact 無法可靠解析。"));
        }
        if (diagnostics.Count > 0) gaps.Add($"{artifact.RelativePath} 有 {diagnostics.Count} 個 SQL statement 只能保留為未知證據。");
        return new(builder.Result, diagnostics, gaps);
    }

    private static bool TryExtractCreateTable(string sql, int line, DataGraphBuilder graph, string migration)
    {
        var match = CreateTableRegex().Match(sql);
        if (!match.Success) return false;
        var tableName = match.Groups["name"].Value;
        var tableKey = graph.EnsureTable(tableName, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(migration, tableKey, CodeEdgeKind.Migrates, GraphSourceKind.Migration, GraphConfidence.Exact);
        var open = sql.IndexOf('(', match.Index + match.Length);
        var close = open >= 0 ? FindMatchingParenthesis(sql, open) : -1;
        if (close <= open) return true;

        foreach (var segment in SplitTopLevel(sql[(open + 1)..close]))
        {
            var part = segment.Trim();
            if (part.Length == 0) continue;
            var upper = part.ToUpperInvariant();
            if (upper.StartsWith("PRIMARY KEY", StringComparison.Ordinal) || upper.StartsWith("CONSTRAINT", StringComparison.Ordinal) && upper.Contains("PRIMARY KEY", StringComparison.Ordinal))
            {
                AddKeyConstraint(part, tableKey, CodeNodeKind.PrimaryKey, "pk", graph, line);
                continue;
            }
            if (upper.StartsWith("FOREIGN KEY", StringComparison.Ordinal) || upper.StartsWith("CONSTRAINT", StringComparison.Ordinal) && upper.Contains("FOREIGN KEY", StringComparison.Ordinal))
            {
                AddForeignKey(part, tableKey, graph, line);
                continue;
            }
            if (upper.StartsWith("UNIQUE", StringComparison.Ordinal) || upper.StartsWith("CHECK", StringComparison.Ordinal))
            {
                AddKeyConstraint(part, tableKey, CodeNodeKind.Constraint, "constraint", graph, line);
                continue;
            }

            var columnMatch = ColumnRegex().Match(part);
            if (!columnMatch.Success) continue;
            var columnName = CleanIdentifier(columnMatch.Groups["name"].Value);
            var columnKey = $"column:{tableKey[6..]}.{DataGraphBuilder.Normalize(columnName)}";
            graph.AddNode(columnKey, CodeNodeKind.Column, columnName, "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line, signature: part);
            graph.AddEdge(tableKey, columnKey, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
            if (upper.Contains("PRIMARY KEY", StringComparison.Ordinal))
            {
                var pk = graph.AddNode($"pk:{tableKey[6..]}.{DataGraphBuilder.Normalize(columnName)}", CodeNodeKind.PrimaryKey, $"PK_{columnName}", "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line);
                graph.AddEdge(tableKey, pk, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
                graph.AddEdge(pk, columnKey, CodeEdgeKind.References, GraphSourceKind.Migration, GraphConfidence.Exact);
            }
            if (upper.Contains("REFERENCES", StringComparison.Ordinal)) AddInlineForeignKey(part, tableKey, columnKey, graph, line);
        }
        return true;
    }

    private static bool TryExtractCreateView(string sql, int line, DataGraphBuilder graph, string migration)
    {
        var match = CreateViewRegex().Match(sql);
        if (!match.Success) return false;
        var (schema, name) = DataGraphBuilder.SplitObject(match.Groups["name"].Value);
        var schemaKey = graph.EnsureSchema(schema);
        var viewKey = graph.AddNode($"view:{DataGraphBuilder.Normalize(schema)}.{DataGraphBuilder.Normalize(name)}", CodeNodeKind.View, name, "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line);
        graph.AddEdge(schemaKey, viewKey, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(migration, viewKey, CodeEdgeKind.Migrates, GraphSourceKind.Migration, GraphConfidence.Exact);
        foreach (Match read in FromJoinRegex().Matches(sql)) graph.AddEdge(viewKey, graph.EnsureTable(read.Groups["name"].Value), CodeEdgeKind.Reads, GraphSourceKind.Sql, GraphConfidence.Resolved);
        return true;
    }

    private static bool TryExtractCreateIndex(string sql, int line, DataGraphBuilder graph, string migration)
    {
        var match = CreateIndexRegex().Match(sql);
        if (!match.Success) return false;
        var table = graph.EnsureTable(match.Groups["table"].Value, GraphSourceKind.Migration, GraphConfidence.Exact);
        var name = CleanIdentifier(match.Groups["name"].Value);
        var key = graph.AddNode($"index:{table[6..]}.{DataGraphBuilder.Normalize(name)}", CodeNodeKind.Index, name, "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line, signature: sql.Trim());
        graph.AddEdge(table, key, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(migration, key, CodeEdgeKind.Migrates, GraphSourceKind.Migration, GraphConfidence.Exact);
        return true;
    }

    private static bool TryExtractProcedure(string sql, int line, DataGraphBuilder graph, string migration)
    {
        var match = CreateProcedureRegex().Match(sql);
        if (!match.Success) return false;
        var (schema, name) = DataGraphBuilder.SplitObject(match.Groups["name"].Value);
        var schemaKey = graph.EnsureSchema(schema);
        var procedure = graph.AddNode($"procedure:{DataGraphBuilder.Normalize(schema)}.{DataGraphBuilder.Normalize(name)}", CodeNodeKind.Procedure, name, "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line);
        graph.AddEdge(schemaKey, procedure, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(migration, procedure, CodeEdgeKind.Migrates, GraphSourceKind.Migration, GraphConfidence.Exact);
        AddReadWriteEdges(sql, procedure, graph);
        return true;
    }

    private static bool TryExtractAlterTable(string sql, int line, DataGraphBuilder graph, string migration)
    {
        var match = AlterTableRegex().Match(sql);
        if (!match.Success) return false;
        var table = graph.EnsureTable(match.Groups["name"].Value, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(migration, table, CodeEdgeKind.Migrates, GraphSourceKind.Migration, GraphConfidence.Exact);
        if (sql.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)) AddForeignKey(sql, table, graph, line);
        return true;
    }

    private static bool TryExtractQuery(string sql, int line, int index, DataGraphBuilder graph, string fileKey,
        string relativePath)
    {
        if (!SelectRegex().IsMatch(sql) && !WriteRegex().IsMatch(sql)) return false;
        var query = graph.AddNode($"query:{DataGraphBuilder.Normalize(relativePath)}:{line}:{index}",
            CodeNodeKind.Query, $"Query at line {line}", "sql", GraphSourceKind.Sql,
            GraphConfidence.Resolved, line, signature: Truncate(sql.Trim(), 500),
            reason: "Standalone SQL query statement.");
        graph.AddEdge(fileKey, query, CodeEdgeKind.Contains, GraphSourceKind.Sql,
            GraphConfidence.Exact, "SQL query declared in this artifact.");
        AddReadWriteEdges(sql, query, graph);
        AddQueryColumnEdges(sql, query, graph, line);
        return true;
    }

    private static void AddReadWriteEdges(string sql, string source, DataGraphBuilder graph)
    {
        foreach (Match match in FromJoinRegex().Matches(sql)) graph.AddEdge(source, graph.EnsureTable(match.Groups["name"].Value), CodeEdgeKind.Reads, GraphSourceKind.Sql, GraphConfidence.Resolved);
        foreach (Match match in InsertRegex().Matches(sql)) graph.AddEdge(source, graph.EnsureTable(match.Groups["name"].Value), CodeEdgeKind.Writes, GraphSourceKind.Sql, GraphConfidence.Resolved);
        foreach (Match match in UpdateRegex().Matches(sql)) graph.AddEdge(source, graph.EnsureTable(match.Groups["name"].Value), CodeEdgeKind.Writes, GraphSourceKind.Sql, GraphConfidence.Resolved);
        foreach (Match match in DeleteRegex().Matches(sql)) graph.AddEdge(source, graph.EnsureTable(match.Groups["name"].Value), CodeEdgeKind.Writes, GraphSourceKind.Sql, GraphConfidence.Resolved);
    }

    private static void AddQueryColumnEdges(string sql, string query, DataGraphBuilder graph, int line)
    {
        var readTables = FromJoinRegex().Matches(sql).Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (readTables.Count == 1)
        {
            var table = graph.EnsureTable(readTables[0]);
            var select = SelectListRegex().Match(sql);
            if (select.Success)
            {
                foreach (var expression in select.Groups["columns"].Value.Split(','))
                {
                    var column = SimpleColumn(expression);
                    if (column is null) continue;
                    var columnKey = graph.EnsureColumn(table, column, line: line,
                        reason: "Column inferred from a single-table SELECT list.");
                    graph.AddEdge(query, columnKey, CodeEdgeKind.Reads, GraphSourceKind.Sql,
                        GraphConfidence.Resolved, "Single-table SELECT column.");
                }
            }
        }

        var writeTables = InsertRegex().Matches(sql).Concat(UpdateRegex().Matches(sql))
            .Select(match => match.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (writeTables.Count != 1) return;
        var writeTable = graph.EnsureTable(writeTables[0]);
        var insert = InsertColumnsRegex().Match(sql);
        if (insert.Success)
        {
            foreach (var raw in insert.Groups["columns"].Value.Split(','))
            {
                var column = SimpleColumn(raw);
                if (column is null) continue;
                var columnKey = graph.EnsureColumn(writeTable, column, line: line,
                    reason: "Column inferred from INSERT column list.");
                graph.AddEdge(query, columnKey, CodeEdgeKind.Writes, GraphSourceKind.Sql,
                    GraphConfidence.Resolved, "INSERT target column.");
            }
        }
        foreach (Match update in UpdateColumnRegex().Matches(sql))
        {
            var columnKey = graph.EnsureColumn(writeTable, update.Groups["column"].Value, line: line,
                reason: "Column inferred from UPDATE SET clause.");
            graph.AddEdge(query, columnKey, CodeEdgeKind.Writes, GraphSourceKind.Sql,
                GraphConfidence.Resolved, "UPDATE target column.");
        }
    }

    private static void AddKeyConstraint(string text, string table, CodeNodeKind kind, string prefix, DataGraphBuilder graph, int line)
    {
        var nameMatch = ConstraintNameRegex().Match(text);
        var name = nameMatch.Success ? CleanIdentifier(nameMatch.Groups["name"].Value) : $"{prefix}_{StableId(text)}";
        var key = graph.AddNode($"{prefix}:{table[6..]}.{DataGraphBuilder.Normalize(name)}", kind, name, "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line, signature: text);
        graph.AddEdge(table, key, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
    }

    private static void AddForeignKey(string text, string table, DataGraphBuilder graph, int line)
    {
        var reference = ReferencesRegex().Match(text);
        if (!reference.Success) return;
        var nameMatch = ConstraintNameRegex().Match(text);
        var name = nameMatch.Success ? CleanIdentifier(nameMatch.Groups["name"].Value) : $"fk_{StableId(text)}";
        var key = graph.AddNode($"fk:{table[6..]}.{DataGraphBuilder.Normalize(name)}", CodeNodeKind.ForeignKey, name, "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line, signature: text);
        graph.AddEdge(table, key, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(key, graph.EnsureTable(reference.Groups["table"].Value), CodeEdgeKind.ForeignKeyTo, GraphSourceKind.Migration, GraphConfidence.Exact);
    }

    private static void AddInlineForeignKey(string text, string table, string column, DataGraphBuilder graph, int line)
    {
        var reference = ReferencesRegex().Match(text);
        if (!reference.Success) return;
        var key = graph.AddNode($"fk:{column[7..]}", CodeNodeKind.ForeignKey, $"FK_{column.Split('.').Last()}", "sql", GraphSourceKind.Migration, GraphConfidence.Exact, line, signature: text);
        graph.AddEdge(table, key, CodeEdgeKind.Contains, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(key, column, CodeEdgeKind.References, GraphSourceKind.Migration, GraphConfidence.Exact);
        graph.AddEdge(key, graph.EnsureTable(reference.Groups["table"].Value), CodeEdgeKind.ForeignKeyTo, GraphSourceKind.Migration, GraphConfidence.Exact);
    }

    private static IReadOnlyList<SqlStatement> SplitStatements(string sql)
    {
        var result = new List<SqlStatement>(); var start = 0; var line = 1; var startLine = 1; var quote = '\0'; var depth = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i]; if (c == '\n') line++;
            if (quote != '\0') { if (c == quote && (i == 0 || sql[i - 1] != '\\')) quote = '\0'; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '(') depth++; else if (c == ')') depth = Math.Max(0, depth - 1);
            if (c != ';' || depth != 0) continue;
            var value = sql[start..i].Trim(); if (value.Length > 0) result.Add(new(value, startLine));
            start = i + 1; startLine = line;
        }
        var tail = sql[start..].Trim(); if (tail.Length > 0) result.Add(new(tail, startLine));
        return result;
    }

    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var start = 0; var depth = 0; var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote && (i == 0 || value[i - 1] != '\\')) quote = '\0'; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '(') depth++; else if (c == ')') depth--;
            else if (c == ',' && depth == 0) { yield return value[start..i]; start = i + 1; }
        }
        yield return value[start..];
    }

    private static int FindMatchingParenthesis(string value, int open)
    {
        var depth = 0; var quote = '\0';
        for (var i = open; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote && value[i - 1] != '\\') quote = '\0'; continue; }
            if (c is '\'' or '"' or '`') { quote = c; continue; }
            if (c == '(') depth++; else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static string RemoveComments(string sql) => BlockCommentRegex().Replace(LineCommentRegex().Replace(sql, ""), "");
    private static bool IsMigrationStatement(string value) => MigrationStatementRegex().IsMatch(value);
    private static bool ContainsDataKeyword(string value) => Regex.IsMatch(value, @"\b(TABLE|VIEW|SELECT|INSERT|UPDATE|DELETE|MIGRATION|COLUMN)\b", RegexOptions.IgnoreCase);
    private static string CleanIdentifier(string value) => value.Trim().Trim('[', ']', '`', '"');
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
    private static string StableId(string value)
    {
        var canonical = Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
    }

    private static string? SimpleColumn(string expression)
    {
        var value = Regex.Replace(expression.Trim(), @"\s+AS\s+.+$", "", RegexOptions.IgnoreCase).Trim();
        if (value == "*" || value.Contains('(')) return null;
        value = value.Split('.').Last().Trim('[', ']', '`', '"');
        return SimpleIdentifierRegex().IsMatch(value) ? value : null;
    }
    private sealed record SqlStatement(string Text, int Line);

    private const string Identifier = """(?:\[[^\]]+\]|`[^`]+`|"[^"]+"|[A-Za-z_][A-Za-z0-9_$]*)(?:\s*\.\s*(?:\[[^\]]+\]|`[^`]+`|"[^"]+"|[A-Za-z_][A-Za-z0-9_$]*))*""";
    [GeneratedRegex(@"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex CreateTableRegex();
    [GeneratedRegex(@"\bCREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex CreateViewRegex();
    [GeneratedRegex(@"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?<name>" + Identifier + @")\s+ON\s+(?<table>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex CreateIndexRegex();
    [GeneratedRegex(@"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:PROCEDURE|PROC)\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex CreateProcedureRegex();
    [GeneratedRegex(@"\bALTER\s+TABLE\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex AlterTableRegex();
    [GeneratedRegex(@"^\s*(?<name>" + Identifier + @")\s+(?<type>[A-Za-z][A-Za-z0-9_]*(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase)] private static partial Regex ColumnRegex();
    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex FromJoinRegex();
    [GeneratedRegex(@"\bINSERT\s+INTO\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex InsertRegex();
    [GeneratedRegex(@"\bUPDATE\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex UpdateRegex();
    [GeneratedRegex(@"\bDELETE\s+FROM\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex DeleteRegex();
    [GeneratedRegex(@"\bREFERENCES\s+(?<table>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex ReferencesRegex();
    [GeneratedRegex(@"\bCONSTRAINT\s+(?<name>" + Identifier + @")", RegexOptions.IgnoreCase)] private static partial Regex ConstraintNameRegex();
    [GeneratedRegex(@"\bSELECT\b", RegexOptions.IgnoreCase)] private static partial Regex SelectRegex();
    [GeneratedRegex(@"\b(?:INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase)] private static partial Regex WriteRegex();
    [GeneratedRegex(@"^\s*(?:CREATE|ALTER|DROP|RENAME|TRUNCATE)\b", RegexOptions.IgnoreCase)] private static partial Regex MigrationStatementRegex();
    [GeneratedRegex(@"\bSELECT\s+(?<columns>.+?)\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex SelectListRegex();
    [GeneratedRegex(@"\bINSERT\s+INTO\s+[^\s(]+\s*\((?<columns>[^)]+)\)", RegexOptions.IgnoreCase)] private static partial Regex InsertColumnsRegex();
    [GeneratedRegex(@"(?:\bSET\b|,)\s*(?<column>[A-Za-z_][A-Za-z0-9_$]*)\s*=", RegexOptions.IgnoreCase)] private static partial Regex UpdateColumnRegex();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_$]*$")] private static partial Regex SimpleIdentifierRegex();
    [GeneratedRegex(@"--[^\r\n]*")] private static partial Regex LineCommentRegex();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex BlockCommentRegex();
}
