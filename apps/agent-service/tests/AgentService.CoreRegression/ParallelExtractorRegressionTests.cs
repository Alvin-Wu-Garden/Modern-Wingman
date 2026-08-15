using System.Reflection;
using System.Collections.Concurrent;
using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;
using AgentService.Modules.GraphRAG.FblAuthority;
using AgentService.Modules.GraphRAG.ParallelExtractor;
using Microsoft.Data.Sqlite;
using Xunit;
using AuthorityGraphNode = AgentService.Modules.GraphRAG.FblAuthority.GraphNode;
using AuthorityGraphRelationship = AgentService.Modules.GraphRAG.FblAuthority.GraphRelationship;

namespace AgentService.CoreRegression;

/// <summary>鎖定 ParallelExtractor 移植後最容易只通過數量、卻遺失屬性語意的邊界。</summary>
public sealed class ParallelExtractorRegressionTests
{
    [Fact]
    public void Community預熱_重啟後不得越過固定前三名()
    {
        var reports = Enumerable.Range(1, 6)
            .Select(index => Community(
                $"c{index}",
                memberCount: 100 - index,
                summaryState: index <= 3
                    ? GraphCommunitySummaryStates.AiReady
                    : GraphCommunitySummaryStates.Template))
            .ToArray();

        var candidates = GraphCommunityAiService.SelectPrewarmCandidates(reports);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task SqliteSchemaOnly_只抽TableViewColumn與ForeignKey()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"wingman-sqlite-schema-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys = ON;
                    CREATE TABLE parent (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                    CREATE TABLE child (
                        id INTEGER PRIMARY KEY,
                        parent_id INTEGER,
                        FOREIGN KEY(parent_id) REFERENCES parent(id));
                    CREATE VIEW child_view AS
                        SELECT child.id, parent.name
                        FROM child JOIN parent ON parent.id = child.parent_id;
                    CREATE INDEX idx_child_parent ON child(parent_id);
                    CREATE TRIGGER trg_child_insert AFTER INSERT ON child BEGIN
                        UPDATE child SET parent_id = NEW.parent_id WHERE id = NEW.id;
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString;
            var catalog = await new ProjectGraphDatabaseExtractor()
                .LoadSqliteDatabaseObjectsAsync(new GraphDatabaseSource(
                    ProjectDatabaseProvider.Sqlite,
                    connectionString,
                    "schema-test"));

            Assert.Equal(
                [("child", "Table"), ("parent", "Table"), ("child_view", "View")],
                catalog.Objects.Select(item => (item.Name, item.ObjectType)).ToArray());
            Assert.DoesNotContain(catalog.Objects, item =>
                item.Name is "idx_child_parent" or "trg_child_insert");
            Assert.Contains(catalog.Columns, item =>
                item.ObjectName == "parent" && item.Name == "id" && item.IsPrimaryKey);
            var foreignKey = Assert.Single(catalog.ForeignKeys);
            Assert.Equal("child", foreignKey.SourceTable);
            Assert.Equal("parent_id", foreignKey.SourceColumn);
            Assert.Equal("parent", foreignKey.TargetTable);
            Assert.Equal("id", foreignKey.TargetColumn);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void BackendFragments_必須依Solution順序合併而不是依Worker完成順序()
    {
        var initial = new CodeGraphData();
        initial.AddNode("Solution", "solution");
        initial.AddNode("Project", "project-1");
        initial.AddNode("Project", "project-2");

        var project1 = new CodeGraphData();
        project1.AddNode("Method", "shared-method", new Dictionary<string, object?>
        {
            ["kind"] = "method",
        });
        project1.AddRelationship("CONTAINS_FILE", "project-1", "file-1");
        var project2 = new CodeGraphData();
        project2.AddNode("Method", "shared-method", new Dictionary<string, object?>
        {
            ["kind"] = "ordinary",
        });
        project2.AddRelationship("CONTAINS_FILE", "project-2", "file-2");

        // 刻意以完成順序的反序放入；結果仍必須由 Solution 順序決定。
        var fragments = new ConcurrentDictionary<string, CodeGraphData>(StringComparer.Ordinal)
        {
            ["project-2"] = project2,
            ["project-1"] = project1,
        };
        var merged = ParallelExtractionEngine.MergeBackendFragmentsInSolutionOrder(initial, fragments);

        var method = Assert.Single(merged.Nodes, node => node.Id == "shared-method");
        Assert.Equal("ordinary", method.Properties["kind"]);
        Assert.Empty(fragments);
    }

    [Fact]
    public void CommunityBuilder_應具確定性並排除孤立節點()
    {
        var nodes = new[]
        {
            Node("a", "A"),
            Node("b", "B"),
            Node("c", "C"),
            Node("isolated", "Isolated"),
        };
        var relationships = new[]
        {
            Relationship("a", "b", 4),
            Relationship("b", "c", 2),
            Relationship("c", "a", 1),
        };
        var document = new GraphDocument(Metadata(), nodes, relationships);

        var first = FblAuthorityCommunityBuilder.Build(document);
        var second = FblAuthorityCommunityBuilder.Build(document);

        Assert.Equal(
            first.Select(report => (report.CommunityId, string.Join('|', report.MemberIds))),
            second.Select(report => (report.CommunityId, string.Join('|', report.MemberIds))));
        Assert.DoesNotContain(first.SelectMany(report => report.MemberIds), id => id == "isolated");
        Assert.Equal(["a", "b", "c"], first.SelectMany(report => report.MemberIds).Order().ToArray());
    }

    [Fact]
    public void CommunityBuilder_取消時應立即中止()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var document = new GraphDocument(
            Metadata(),
            [Node("a", "A"), Node("b", "B")],
            [Relationship("a", "b", 1)]);

        Assert.Throws<OperationCanceledException>(() =>
            FblAuthorityCommunityBuilder.Build(document, cancellation.Token));
    }

    [Fact]
    public void ApplyNeo4jWrite_跨Fragment重複關係_應重現原始Merge覆寫語意()
    {
        var firstFragment = new CodeGraphData();
        firstFragment.AddRelationship(
            "CALLS",
            "source",
            "target",
            new Dictionary<string, object?>
            {
                ["firstOnly"] = "keep",
                ["shared"] = "old",
                ["locations"] = new[] { "a.cs:10" },
            });
        firstFragment.AddRelationship("CALLS", "source", "target");

        var secondFragment = new CodeGraphData();
        secondFragment.AddRelationship(
            "CALLS",
            "source",
            "target",
            new Dictionary<string, object?>
            {
                ["shared"] = "new",
                ["locations"] = new[] { "b.cs:20" },
            });

        var merged = new CodeGraphData();
        merged.ApplyNeo4jWrite(Assert.Single(firstFragment.Relationships));
        merged.ApplyNeo4jWrite(Assert.Single(secondFragment.Relationships));

        var relationship = Assert.Single(merged.Relationships);
        Assert.Equal(1, relationship.OccurrenceCount);
        Assert.Equal("keep", relationship.Properties["firstOnly"]);
        Assert.Equal("new", relationship.Properties["shared"]);
        Assert.Equal(["b.cs:20"], relationship.Locations);
    }

    [Fact]
    public void ApplyNeo4jIntegrationWrite_資料庫Writer不得新增Locations()
    {
        var backend = new CodeGraphData();
        backend.AddRelationship(
            "CALLS",
            "source",
            "target",
            new Dictionary<string, object?>
            {
                ["locations"] = new[] { "backend.cs:10" },
                ["phase"] = "backend",
            });
        var laterPhase = new CodeGraphData();
        laterPhase.AddRelationship(
            "CALLS",
            "source",
            "target",
            new Dictionary<string, object?>
            {
                ["locations"] = new[] { "database.sql:20" },
                ["phase"] = "database",
            });

        var merged = new CodeGraphData();
        merged.ApplyNeo4jWrite(Assert.Single(backend.Relationships));
        merged.ApplyNeo4jIntegrationWrite(Assert.Single(laterPhase.Relationships));

        var relationship = Assert.Single(merged.Relationships);
        Assert.Equal("database", relationship.Properties["phase"]);
        Assert.Equal(["backend.cs:10"], relationship.Locations);
    }

    [Fact]
    public void ToNeo4jProperties_ParallelProjectId_不得被ModernScope吃掉()
    {
        var method = typeof(Neo4jGraphStore).GetMethod(
            "ToNeo4jProperties",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var raw = new Dictionary<string, object?>
        {
            ["projectId"] = "parallel-project-id",
            ["wingmanProjectId"] = "不可由抽取器覆寫",
            ["kind"] = "class",
        };

        var result = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            method.Invoke(null, [raw]));

        Assert.Equal("parallel-project-id", result["projectId"]);
        Assert.Equal("class", result["kind"]);
        Assert.False(result.ContainsKey("wingmanProjectId"));
    }

    [Fact]
    public void Neo4jGraphEntityIdentity_應使用獨立WingmanScope且不搬移舊資料()
    {
        var schema = Assert.IsType<string[]>(typeof(Neo4jGraphStore).GetField(
            "SchemaStatements",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        var cleanup = Assert.IsType<string[]>(typeof(Neo4jGraphStore).GetField(
            "LegacySchemaCleanupStatements",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));

        Assert.Contains(schema, statement =>
            statement.Contains("n.wingmanProjectId, n.graphVersion, n.id", StringComparison.Ordinal));
        Assert.Contains("DROP CONSTRAINT entity_key IF EXISTS", cleanup);
        Assert.Null(typeof(Neo4jGraphStore).GetField(
            "GraphEntityScopeMigrationCypher",
            BindingFlags.Static | BindingFlags.NonPublic));
    }

    [Fact]
    public void ReadOnlyCypher_必須使用WingmanScope而不是Parallel原始ProjectId()
    {
        const string valid = """
            MATCH (n:GraphEntity {wingmanProjectId: $projectId, graphVersion: $graphVersion})
            RETURN n LIMIT $limit
            """;
        Assert.Equal(valid, Neo4jGraphStore.EnsureReadOnlyCypher(valid));

        const string unsafeScope = """
            MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})
            RETURN n LIMIT $limit
            """;
        Assert.Throws<InvalidOperationException>(() =>
            Neo4jGraphStore.EnsureReadOnlyCypher(unsafeScope));
    }

    private static AuthorityGraphNode Node(string key, string name) =>
        AuthorityGraphNode.Create(GraphNodeKind.Type, key, new Dictionary<string, object?> { ["name"] = name });

    private static AuthorityGraphRelationship Relationship(string source, string target, int occurrenceCount) =>
        AuthorityGraphRelationship.Create(
            GraphRelationshipKind.Calls,
            source,
            target,
            new Dictionary<string, object?> { ["occurrenceCount"] = occurrenceCount });

    private static GraphRunMetadata Metadata() =>
        new("test", DateTimeOffset.UnixEpoch, "C:\\src", "test", null, null);

    private static GraphCommunityReportV4 Community(
        string id,
        int memberCount,
        string summaryState) =>
        new(
            id,
            "C0",
            null,
            true,
            id,
            id,
            summaryState,
            0,
            [id],
            memberCount,
            [],
            [],
            $"cache-{id}",
            false,
            0,
            new Dictionary<string, string>());
}
