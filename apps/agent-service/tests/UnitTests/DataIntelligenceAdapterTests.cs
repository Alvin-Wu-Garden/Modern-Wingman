using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

namespace AgentService.UnitTests;

public sealed class DataIntelligenceAdapterTests
{
    [Fact]
    public void CSharpOrmAndEmbeddedQueryUseCanonicalCodeKeysAndTraceablePaths()
    {
        const string content = """
            namespace Acme.Orders;

            [Table("orders", Schema = "sales")]
            public sealed class Order
            {
                public object Load()
                {
                    return FromSqlRaw("SELECT Id, Total FROM sales.orders");
                }
            }
            """;
        var artifact = Artifact("src/Order.cs", content);

        var result = new OrmDataArtifactAdapter().Analyze(artifact).Graph;
        const string type = "Acme.Orders.Order";
        const string table = "table:sales.orders";
        var query = Assert.Single(result.Nodes, node => node.Kind == CodeNodeKind.Query);

        Assert.Contains(result.Nodes, node => node.Key == type && node.Kind == CodeNodeKind.Type);
        Assert.Contains(result.Edges, edge => edge.SourceKey == type && edge.TargetKey == table && edge.Kind == CodeEdgeKind.MapsTo);
        Assert.Contains(result.Edges, edge => edge.SourceKey == "file:src/Order.cs" && edge.TargetKey == query.Key && edge.Kind == CodeEdgeKind.Contains);
        Assert.Contains(result.Edges, edge => edge.SourceKey == "Acme.Orders.Order.Load()" && edge.TargetKey == query.Key && edge.Kind == CodeEdgeKind.Contains);
        Assert.Contains(result.Edges, edge => edge.SourceKey == query.Key && edge.TargetKey == table && edge.Kind == CodeEdgeKind.Reads);
        Assert.Contains(result.Edges, edge => edge.SourceKey == query.Key && edge.TargetKey == "column:sales.orders.id" && edge.Kind == CodeEdgeKind.Reads);
        Assert.Contains(result.Edges, edge => edge.SourceKey == $"response-contract:{type}" && edge.TargetKey == table && edge.Kind == CodeEdgeKind.SerializesTo);
        AssertCompleteProvenance(result, artifact.ContentHash);
        Assert.True(PathExists(result, type, table));
        Assert.True(PathExists(result, "Acme.Orders.Order.Load()", table));
    }

    [Fact]
    public void JavaOrmAndEmbeddedQueryUseCanonicalCodeKeys()
    {
        const string content = """
            package com.acme.orders;

            @Table(name = "orders", schema = "sales")
            public class Order {
                public void load() {
                    jdbc.query("SELECT id FROM sales.orders");
                }
            }
            """;
        var artifact = Artifact("src/main/java/com/acme/orders/Order.java", content);

        var result = new OrmDataArtifactAdapter().Analyze(artifact).Graph;
        const string type = "com.acme.orders.Order";
        const string table = "table:sales.orders";
        var query = Assert.Single(result.Nodes, node => node.Kind == CodeNodeKind.Query);

        Assert.Contains(result.Edges, edge => edge.SourceKey == type && edge.TargetKey == table && edge.Kind == CodeEdgeKind.MapsTo);
        Assert.Contains(result.Edges, edge => edge.SourceKey == "com.acme.orders.Order.load()" && edge.TargetKey == query.Key && edge.Kind == CodeEdgeKind.Contains);
        Assert.True(PathExists(result, type, table));
        Assert.True(PathExists(result, "com.acme.orders.Order.load()", table));
        AssertCompleteProvenance(result, artifact.ContentHash);
    }

    [Fact]
    public void StandaloneQueryIsSqlEvidenceAndIsNotMisclassifiedAsMigration()
    {
        const string content = "SELECT Id, Name FROM sales.Customers WHERE Id = @id;";
        var artifact = Artifact("sql/customer-query.sql", content);

        var result = new SqlDataArtifactAdapter().Analyze(artifact).Graph;
        var query = Assert.Single(result.Nodes, node => node.Kind == CodeNodeKind.Query);

        Assert.DoesNotContain(result.Nodes, node => node.Kind == CodeNodeKind.Migration);
        Assert.Equal(GraphSourceKind.Sql, query.SourceKind);
        Assert.NotEqual(GraphConfidence.Exact, query.Confidence);
        Assert.Contains(result.Edges, edge => edge.SourceKey == "file:sql/customer-query.sql" && edge.TargetKey == query.Key && edge.Kind == CodeEdgeKind.Contains);
        Assert.Contains(result.Edges, edge => edge.SourceKey == query.Key && edge.TargetKey == "table:sales.customers" && edge.Kind == CodeEdgeKind.Reads);
        Assert.Contains(result.Edges, edge => edge.SourceKey == query.Key && edge.TargetKey == "column:sales.customers.id" && edge.Kind == CodeEdgeKind.Reads);
        AssertCompleteProvenance(result, artifact.ContentHash);
    }

    [Fact]
    public void DdlIsMigrationAndAnonymousConstraintIdsAreStableSha256Values()
    {
        const string content = """
            CREATE TABLE sales.Orders (
                Id bigint NOT NULL,
                CustomerId bigint NOT NULL,
                PRIMARY KEY (Id),
                FOREIGN KEY (CustomerId) REFERENCES sales.Customers(Id),
                UNIQUE (CustomerId)
            );
            """;
        var artifact = Artifact("migrations/001_orders.sql", content);
        var adapter = new SqlDataArtifactAdapter();

        var first = adapter.Analyze(artifact).Graph;
        var second = adapter.Analyze(artifact).Graph;
        var firstAnonymousKeys = first.Nodes.Where(node => node.Kind is CodeNodeKind.PrimaryKey or CodeNodeKind.ForeignKey or CodeNodeKind.Constraint)
            .Select(node => node.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var secondAnonymousKeys = second.Nodes.Where(node => node.Kind is CodeNodeKind.PrimaryKey or CodeNodeKind.ForeignKey or CodeNodeKind.Constraint)
            .Select(node => node.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();

        var migration = Assert.Single(first.Nodes, node => node.Kind == CodeNodeKind.Migration);
        Assert.Equal(GraphSourceKind.Migration, migration.SourceKind);
        Assert.Equal(GraphConfidence.Exact, migration.Confidence);
        Assert.NotEmpty(firstAnonymousKeys);
        Assert.Equal(firstAnonymousKeys, secondAnonymousKeys);
        Assert.All(firstAnonymousKeys, key => Assert.Matches("_(?:[0-9a-f]{16})(?:$|\\.)", key));
        AssertCompleteProvenance(first, artifact.ContentHash);
    }

    [Fact]
    public void QueryBeforeDdlPreservesBothResolvedAndExactTableEvidence()
    {
        var artifact = Artifact("migrations/query-before-ddl.sql", """
            SELECT Id FROM sales.Orders;
            CREATE TABLE sales.Orders (Id bigint PRIMARY KEY);
            """);

        var result = new SqlDataArtifactAdapter().Analyze(artifact).Graph;
        var contributions = result.Nodes
            .Where(node => node.Key == "table:sales.orders")
            .ToList();

        Assert.Contains(contributions, node =>
            node.SourceKind == GraphSourceKind.Sql && node.Confidence == GraphConfidence.Resolved);
        Assert.Contains(contributions, node =>
            node.SourceKind == GraphSourceKind.Migration && node.Confidence == GraphConfidence.Exact);
        Assert.Contains(result.Edges, edge =>
            edge.SourceKey == "schema:sales" &&
            edge.TargetKey == "table:sales.orders" &&
            edge.SourceKind == GraphSourceKind.Migration &&
            edge.Confidence == GraphConfidence.Exact);
    }

    private static DataArtifact Artifact(string relativePath, string content)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new(relativePath, relativePath, content, hash);
    }

    private static bool PathExists(CodeAnalysisResult graph, string source, string target)
    {
        var adjacency = graph.Edges.GroupBy(edge => edge.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetKey).ToList(), StringComparer.Ordinal);
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { source };
        pending.Enqueue(source);
        while (pending.TryDequeue(out var current))
        {
            if (current == target) return true;
            if (!adjacency.TryGetValue(current, out var next)) continue;
            foreach (var candidate in next)
                if (visited.Add(candidate)) pending.Enqueue(candidate);
        }
        return false;
    }

    private static void AssertCompleteProvenance(CodeAnalysisResult result, string contentHash)
    {
        Assert.All(result.Nodes, node =>
        {
            Assert.False(string.IsNullOrWhiteSpace(node.ExtractorId));
            Assert.False(string.IsNullOrWhiteSpace(node.ExtractorVersion));
            Assert.NotNull(node.IndexedAt);
            Assert.Equal(contentHash, node.ContentHash);
        });
        Assert.All(result.Edges, edge =>
        {
            Assert.False(string.IsNullOrWhiteSpace(edge.ExtractorId));
            Assert.False(string.IsNullOrWhiteSpace(edge.ExtractorVersion));
            Assert.NotNull(edge.IndexedAt);
            Assert.Equal(contentHash, edge.ContentHash);
        });
    }
}
