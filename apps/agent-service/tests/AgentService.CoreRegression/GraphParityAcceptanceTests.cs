using System.Collections;
using System.Globalization;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.Persistence;
using AgentService.Modules.GraphRAG;
using AgentService.Modules.GraphRAG.ExtractedGraph;
using AgentService.Modules.GraphRAG.ParallelExtractor;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;
using Xunit;
using ExtractedGraphNode = AgentService.Modules.GraphRAG.ExtractedGraph.GraphNode;
using ExtractedGraphRelationship = AgentService.Modules.GraphRAG.ExtractedGraph.GraphRelationship;

namespace AgentService.CoreRegression;

/// <summary>
/// 以兩個隔離 Neo4j 執行內容級驗收。一般回歸不連外；只有明確設定
/// WINGMAN_GRAPH_PARITY=1 時，才逐筆比較原始 ParallelExtractor 與 Modern Wingman。
/// </summary>
public sealed class GraphParityAcceptanceTests
{
    /// <summary>
    /// 驗證原版 ParallelExtractor 在相同輸入與平行度下是否具有決定性。
    /// 此測試用來區分移植差異與原版本身的平行最後寫入競爭。
    /// </summary>
    [Fact]
    public async Task 原始ParallelExtractor重跑結果_應揭露平行最後寫入競爭()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_COMPARE_ORIGINALS"),
                "1",
                StringComparison.Ordinal))
            return;

        var firstUri = Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_ORIGINAL_URI")
            ?? "bolt://127.0.0.1:17689";
        var secondUri = RequiredEnvironment("WINGMAN_GRAPH_PARITY_SECOND_ORIGINAL_URI");
        await using var first = GraphDatabase.Driver(
            firstUri,
            AuthTokens.None,
            config => config.WithEncryptionLevel(EncryptionLevel.None));
        await using var second = GraphDatabase.Driver(
            secondUri,
            AuthTokens.None,
            config => config.WithEncryptionLevel(EncryptionLevel.None));
        const string nodeQuery =
            """
            MATCH (n:GraphEntity)
            RETURN [label IN labels(n) WHERE label <> 'GraphEntity'][0] AS token,
                   n.id AS source, '' AS target, properties(n) AS properties
            ORDER BY token, source
            """;
        const string edgeQuery =
            """
            MATCH (source:GraphEntity)-[relationship]->(target:GraphEntity)
            RETURN type(relationship) AS token, source.id AS source,
                   target.id AS target, properties(relationship) AS properties
            ORDER BY token, source, target
            """;
        var nodeResult = await CompareAsync(
            first, "neo4j", nodeQuery, new { },
            second, "neo4j", nodeQuery, new { },
            isNode: true,
            normalizeKnownParallelRace: false);
        var edgeResult = await CompareAsync(
            first, "neo4j", edgeQuery, new { },
            second, "neo4j", edgeQuery, new { },
            isNode: false);

        Assert.True(nodeResult.TotalMismatches > 0 && edgeResult.TotalMismatches == 0,
            $"未重現預期的原版平行最後寫入競爭：節點差異 {nodeResult.TotalMismatches:N0}/{nodeResult.Compared:N0}，" +
            $"關係 {edgeResult.TotalMismatches:N0}/{edgeResult.Compared:N0}。" + Environment.NewLine +
            string.Join(Environment.NewLine, nodeResult.Samples.Concat(edgeResult.Samples)));
    }

    /// <summary>
    /// 直接執行目前的 Modern Wingman 抽取器，再與原版 Neo4j 逐筆比較。
    /// 此模式可排除既有 Neo4j snapshot 過舊，造成現行程式被錯誤判定為不一致。
    /// </summary>
    [Fact]
    public async Task 目前抽取器輸出與原始ParallelExtractor_節點關係及屬性必須一對一()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_REEXTRACT"),
                "1",
                StringComparison.Ordinal))
            return;

        var projectId = RequiredEnvironment("WINGMAN_GRAPH_PARITY_PROJECT_ID");
        var projectRoot = RequiredEnvironment("WINGMAN_GRAPH_PARITY_PROJECT_ROOT");
        var solutionPath = Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_SOLUTION_PATH");
        var agentServiceRoot = FindAgentServiceRoot();
        var wingmanDatabase = Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_WINGMAN_DB")
            ?? Path.GetFullPath(Path.Combine(agentServiceRoot, "..", "wingman_dev.db"));
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={wingmanDatabase}")
            .Options;
        var factory = new PooledDbContextFactory<AppDbContext>(options);
        var configurationStore = new ProjectDatabaseConfigurationStore(
            factory,
            new DpapiSecretProtector());
        var sqlServerConfiguration = await configurationStore.GetAsync(
            projectId,
            ProjectDatabaseProvider.SqlServer,
            includePassword: true);
        Assert.NotNull(sqlServerConfiguration);
        var sqlServerSource = ProjectGraphDatabaseSourceProvider.Build(sqlServerConfiguration);
        var pipeline = new ParallelExtractorPipeline(
            new ParallelExtractionEngine(),
            NullLogger<ParallelExtractorPipeline>.Instance);
        var extraction = await pipeline.ExtractAsync(
            projectRoot,
            solutionPath,
            sqlServerSource.ConnectionString,
            includeCodeChunkText: false,
            maxDegreeOfParallelism: 4);

        var originalUri = Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_ORIGINAL_URI")
            ?? "bolt://127.0.0.1:7687";
        var originalDatabase = Environment.GetEnvironmentVariable("WINGMAN_GRAPH_PARITY_ORIGINAL_DATABASE")
            ?? "neo4j";
        await using var original = GraphDatabase.Driver(
            originalUri,
            AuthTokens.None,
            config => config.WithEncryptionLevel(EncryptionLevel.None));
        var nodeResult = await CompareExtractedNodesAsync(
            original,
            originalDatabase,
            extraction.Document.Nodes);
        var edgeResult = await CompareExtractedRelationshipsAsync(
            original,
            originalDatabase,
            extraction.Document.Relationships);

        Assert.True(nodeResult.TotalMismatches == 0 && edgeResult.TotalMismatches == 0,
            $"重新抽取內容不一致：節點 {nodeResult.TotalMismatches:N0}/{nodeResult.Compared:N0}，" +
            $"關係 {edgeResult.TotalMismatches:N0}/{edgeResult.Compared:N0}。" + Environment.NewLine +
            string.Join(Environment.NewLine, nodeResult.Samples.Concat(edgeResult.Samples)));
    }

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
        bool isNode,
        bool normalizeKnownParallelRace = true)
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
            var original = CanonicalRecord(originalCursor.Current, isNode, normalizeKnownParallelRace);
            var modern = CanonicalRecord(modernCursor.Current, isNode, normalizeKnownParallelRace);
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

    private static async Task<ParityResult> CompareExtractedNodesAsync(
        IDriver originalDriver,
        string originalDatabase,
        IReadOnlyList<ExtractedGraphNode> modernNodes)
    {
        await using var session = originalDriver.AsyncSession(config =>
            config.WithDatabase(originalDatabase).WithFetchSize(2_000));
        var cursor = await session.RunAsync(
            """
            MATCH (n:GraphEntity)
            RETURN [label IN labels(n) WHERE label <> 'GraphEntity'][0] AS token,
                   n.id AS source, '' AS target, properties(n) AS properties
            ORDER BY token, source
            """);
        using var modern = modernNodes
            .OrderBy(node => GraphSchema.GetNodeLabel(node.Kind), StringComparer.Ordinal)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .GetEnumerator();
        var samples = new List<string>();
        var mismatches = 0;
        var compared = 0;
        while (true)
        {
            var hasOriginal = await cursor.FetchAsync();
            var hasModern = modern.MoveNext();
            if (!hasOriginal || !hasModern)
            {
                if (hasOriginal != hasModern)
                {
                    mismatches++;
                    samples.Add($"節點筆數不同；原版仍有資料={hasOriginal}，Modern 仍有資料={hasModern}。");
                }
                break;
            }

            compared++;
            var current = modern.Current;
            var token = GraphSchema.GetNodeLabel(current.Kind);
            var originalCanonical = CanonicalRecord(cursor.Current, isNode: true);
            var storedProperties = new Dictionary<string, object?>(current.Properties, StringComparer.Ordinal)
            {
                ["id"] = current.Key,
            };
            var modernCanonical = CanonicalValues(
                token,
                current.Key,
                string.Empty,
                storedProperties,
                isNode: true);
            if (!originalCanonical.Equals(modernCanonical, StringComparison.Ordinal))
            {
                mismatches++;
                if (samples.Count < 20)
                    samples.Add($"{token}|{current.Key}|：原版={originalCanonical}；Modern={modernCanonical}");
            }
        }

        await cursor.ConsumeAsync();
        return new ParityResult(compared, mismatches, samples);
    }

    private static async Task<ParityResult> CompareExtractedRelationshipsAsync(
        IDriver originalDriver,
        string originalDatabase,
        IReadOnlyList<ExtractedGraphRelationship> modernRelationships)
    {
        await using var session = originalDriver.AsyncSession(config =>
            config.WithDatabase(originalDatabase).WithFetchSize(2_000));
        var cursor = await session.RunAsync(
            """
            MATCH (source:GraphEntity)-[relationship]->(target:GraphEntity)
            RETURN type(relationship) AS token, source.id AS source,
                   target.id AS target, properties(relationship) AS properties
            ORDER BY token, source, target
            """);
        using var modern = modernRelationships
            .OrderBy(edge => GraphSchema.GetRelationshipType(edge.Kind), StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceKey, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetKey, StringComparer.Ordinal)
            .GetEnumerator();
        var samples = new List<string>();
        var mismatches = 0;
        var compared = 0;
        while (true)
        {
            var hasOriginal = await cursor.FetchAsync();
            var hasModern = modern.MoveNext();
            if (!hasOriginal || !hasModern)
            {
                if (hasOriginal != hasModern)
                {
                    mismatches++;
                    samples.Add($"關係筆數不同；原版仍有資料={hasOriginal}，Modern 仍有資料={hasModern}。");
                }
                break;
            }

            compared++;
            var current = modern.Current;
            var token = GraphSchema.GetRelationshipType(current.Kind);
            var originalCanonical = CanonicalRecord(cursor.Current, isNode: false);
            var modernCanonical = CanonicalValues(
                token,
                current.SourceKey,
                current.TargetKey,
                current.Properties,
                isNode: false);
            if (!originalCanonical.Equals(modernCanonical, StringComparison.Ordinal))
            {
                mismatches++;
                if (samples.Count < 20)
                {
                    samples.Add(
                        $"{token}|{current.SourceKey}|{current.TargetKey}：" +
                        $"原版={originalCanonical}；Modern={modernCanonical}");
                }
            }
        }

        await cursor.ConsumeAsync();
        return new ParityResult(compared, mismatches, samples);
    }

    private static string CanonicalRecord(
        IRecord record,
        bool isNode,
        bool normalizeKnownParallelRace = true)
    {
        var properties = record["properties"].As<Dictionary<string, object>>();
        var token = record["token"].As<string>();
        return CanonicalValues(
            token,
            record["source"].As<string>(),
            record["target"].As<string>(),
            properties.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
            isNode,
            normalizeKnownParallelRace);
    }

    private static string CanonicalValues(
        string token,
        string source,
        string target,
        IReadOnlyDictionary<string, object?> properties,
        bool isNode,
        bool normalizeKnownParallelRace = true)
    {
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
            .Append(token).Append('|')
            .Append(source).Append('|')
            .Append(target).Append('|');
        foreach (var pair in filtered)
        {
            builder.Append(Escape(pair.Key)).Append('=');
            // 兩次獨立執行原始 ParallelExtractor 已證明 Method.kind 會因平行合併順序，
            // 在語法宣告的 method 與 SemanticModel stub 的 ordinary 之間漂移。
            // 驗收只正規化這個已被原始程式自身重現的競爭；其餘屬性仍逐值比較。
            var value = normalizeKnownParallelRace &&
                        isNode &&
                        token.Equals("Method", StringComparison.Ordinal) &&
                        pair.Key.Equals("kind", StringComparison.Ordinal) &&
                        pair.Value is string methodKind &&
                        methodKind is "method" or "ordinary"
                ? "method"
                : pair.Value;
            AppendValue(builder, value);
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
