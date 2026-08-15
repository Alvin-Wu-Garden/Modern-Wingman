using System.Collections;
using System.Globalization;
using System.Text;
using AgentService.Modules.GraphRAG;
using Microsoft.Extensions.Configuration;
using Neo4j.Driver;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>
/// 以兩個隔離 Neo4j 執行內容級驗收。一般回歸不連外；只有明確設定
/// WINGMAN_GRAPH_PARITY=1 時，才逐筆比較原始 ParallelExtractor 與 Modern Wingman。
/// </summary>
public sealed class GraphParityAcceptanceTests
{
    [Fact]
    public async Task 原始ParallelExtractor與ModernWingman_節點關係及屬性必須一對一()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY"),
                "1",
                StringComparison.Ordinal))
            return;

        var projectId = RequiredEnvironment("WINGMAN_GRAPH_PARITY_PROJECT_ID");
        var graphVersion = RequiredEnvironment("WINGMAN_GRAPH_PARITY_VERSION");
        var agentServiceRoot = FindAgentServiceRoot();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(agentServiceRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();
        var modernUri = configuration["Neo4j:Uri"]
            ?? throw new InvalidOperationException("找不到 Modern Wingman Neo4j URI。");
        var modernUser = configuration["Neo4j:Username"] ?? "neo4j";
        var modernPassword = GraphRagNeo4jCredentialStore.Resolve(configuration);
        var modernDatabase = configuration["Neo4j:Database"] ?? "neo4j";
        var originalUri = Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_ORIGINAL_URI")
            ?? "bolt://127.0.0.1:7687";
        var originalDatabase = Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_ORIGINAL_DATABASE")
            ?? "neo4j";

        await using var original = GraphDatabase.Driver(
            originalUri,
            AuthTokens.None,
            config => config.WithEncryptionLevel(EncryptionLevel.None));
        await using var modern = GraphDatabase.Driver(
            modernUri,
            AuthTokens.Basic(modernUser, modernPassword),
            config => config.WithEncryptionLevel(EncryptionLevel.None));

        var nodeResult = await CompareAsync(
            original,
            originalDatabase,
            """
            MATCH (n:GraphEntity)
            RETURN [label IN labels(n) WHERE label <> 'GraphEntity'][0] AS token,
                   n.id AS source, '' AS target, properties(n) AS properties
            ORDER BY token, source
            """,
            new { },
            modern,
            modernDatabase,
            """
            MATCH (n:GraphEntity {wingmanProjectId: $projectId, graphVersion: $graphVersion})
            RETURN [label IN labels(n) WHERE label <> 'GraphEntity'][0] AS token,
                   n.id AS source, '' AS target, properties(n) AS properties
            ORDER BY token, source
            """,
            new { projectId, graphVersion },
            isNode: true);
        var edgeResult = await CompareAsync(
            original,
            originalDatabase,
            """
            MATCH (source:GraphEntity)-[relationship]->(target:GraphEntity)
            RETURN type(relationship) AS token, source.id AS source,
                   target.id AS target, properties(relationship) AS properties
            ORDER BY token, source, target
            """,
            new { },
            modern,
            modernDatabase,
            """
            MATCH (source:GraphEntity {wingmanProjectId: $projectId, graphVersion: $graphVersion})
                  -[relationship]->
                  (target:GraphEntity {wingmanProjectId: $projectId, graphVersion: $graphVersion})
            RETURN type(relationship) AS token, source.id AS source,
                   target.id AS target, properties(relationship) AS properties
            ORDER BY token, source, target
            """,
            new { projectId, graphVersion },
            isNode: false);
        Assert.True(nodeResult.TotalMismatches == 0 && edgeResult.TotalMismatches == 0,
            $"內容不一致：節點 {nodeResult.TotalMismatches:N0}/{nodeResult.Compared:N0}，" +
            $"關係 {edgeResult.TotalMismatches:N0}/{edgeResult.Compared:N0}。" + Environment.NewLine +
            string.Join(Environment.NewLine, nodeResult.Samples.Concat(edgeResult.Samples)));
    }

    private static async Task<ParityResult> CompareAsync(
        IDriver originalDriver,
        string originalDatabase,
        string originalCypher,
        object originalParameters,
        IDriver modernDriver,
        string modernDatabase,
        string modernCypher,
        object modernParameters,
        bool isNode)
    {
        await using var originalSession = originalDriver.AsyncSession(config =>
            config.WithDatabase(originalDatabase).WithFetchSize(2_000));
        await using var modernSession = modernDriver.AsyncSession(config =>
            config.WithDatabase(modernDatabase).WithFetchSize(2_000));
        var originalCursor = await originalSession.RunAsync(originalCypher, originalParameters);
        var modernCursor = await modernSession.RunAsync(modernCypher, modernParameters);
        var samples = new List<string>();
        var totalMismatches = 0;
        var compared = 0;
        while (true)
        {
            var hasOriginal = await originalCursor.FetchAsync();
            var hasModern = await modernCursor.FetchAsync();
            if (!hasOriginal || !hasModern)
            {
                if (hasOriginal != hasModern)
                {
                    totalMismatches++;
                    samples.Add($"資料筆數不同；原版仍有資料={hasOriginal}，Modern 仍有資料={hasModern}。");
                }
                break;
            }

            compared++;
            var original = CanonicalRecord(originalCursor.Current, isNode);
            var modern = CanonicalRecord(modernCursor.Current, isNode);
            if (!original.Equals(modern, StringComparison.Ordinal))
            {
                totalMismatches++;
                if (samples.Count < 20)
                {
                    var identity = $"{modernCursor.Current["token"].As<string>()}|" +
                                   $"{modernCursor.Current["source"].As<string>()}|" +
                                   modernCursor.Current["target"].As<string>();
                    samples.Add($"{identity}：原版={original}；Modern={modern}");
                }
            }
        }

        await originalCursor.ConsumeAsync();
        await modernCursor.ConsumeAsync();
        return new ParityResult(compared, totalMismatches, samples);
    }

    private static string CanonicalRecord(IRecord record, bool isNode)
    {
        var properties = record["properties"].As<Dictionary<string, object>>();
        var token = record["token"].As<string>();
        var ignoreCodeChunkText = string.Equals(
            Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_IGNORE_CODE_CHUNK_TEXT"),
            "1",
            StringComparison.Ordinal);
        // ParallelExtractor 直接輸出 Roslyn Workspace 的 ProjectId。這個 GUID
        // 每次 OpenSolutionAsync 都會重新產生，因此兩個獨立程序不可能值相同；
        // 屬性仍須存在且為非空 GUID，但內容級比較只排除此一原版自身的揮發值。
        if (isNode && token.Equals("Project", StringComparison.Ordinal))
        {
            Assert.True(properties.TryGetValue("projectGuid", out var projectGuid));
            Assert.True(Guid.TryParse(projectGuid?.ToString(), out _));
        }

        var filtered = properties
            .Where(pair => !pair.Key.Equals("wingmanProjectId", StringComparison.Ordinal) &&
                           !pair.Key.Equals("graphVersion", StringComparison.Ordinal) &&
                           !(isNode && pair.Key.Equals("indexedAtUtc", StringComparison.Ordinal)) &&
                           !(isNode &&
                             token.Equals("Project", StringComparison.Ordinal) &&
                             pair.Key.Equals("projectGuid", StringComparison.Ordinal)) &&
                           !(ignoreCodeChunkText && isNode &&
                             token.Equals("CodeChunk", StringComparison.Ordinal) &&
                             pair.Key is "text" or "textHash" or "truncated"))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal);
        var builder = new StringBuilder()
            .Append(record["token"].As<string>()).Append('|')
            .Append(record["source"].As<string>()).Append('|')
            .Append(record["target"].As<string>()).Append('|');
        foreach (var pair in filtered)
        {
            builder.Append(Escape(pair.Key)).Append('=');
            AppendValue(builder, pair.Value);
            builder.Append(';');
        }
        return builder.ToString();
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;
            case string text:
                builder.Append('s').Append(Escape(text));
                return;
            case bool boolean:
                builder.Append(boolean ? "b1" : "b0");
                return;
            case byte[] bytes:
                builder.Append('x').Append(Convert.ToHexString(bytes));
                return;
            case IEnumerable sequence when value is not IDictionary:
                builder.Append('[');
                foreach (var item in sequence)
                {
                    AppendValue(builder, item);
                    builder.Append(',');
                }
                builder.Append(']');
                return;
            case IFormattable formattable:
                builder.Append('n').Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            default:
                builder.Append('o').Append(Escape(value.ToString() ?? string.Empty));
                return;
        }
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace("=", "\\=", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal);

    private static string FindAgentServiceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "appsettings.json");
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "AgentService.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 AgentService 專案目錄。");
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"內容級驗收缺少環境變數：{name}");

    private sealed record ParityResult(
        int Compared,
        int TotalMismatches,
        IReadOnlyList<string> Samples);
}
