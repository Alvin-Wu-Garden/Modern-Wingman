using AgentService.Host.RestEndpoints;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using AgentService.Modules.GraphRAG;
using System.Text.Json;

namespace AgentService.UnitTests;

public sealed class ReadOnlyGraphAndDatabasePathV3Tests
{
    [Theory]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n LIMIT $limit")]
    [InlineData(
        "MATCH (a:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-->(b:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN a,b LIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) WHERE n.name = 'Insert order' RETURN n LIMIT $limit")]
    public void KnowledgeGraph_AllowsScopedBoundedReadOnlyCypher(string query) =>
        Assert.Equal(query, Neo4jGraphStore.EnsureReadOnlyCypher(query));

    [Theory]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n",
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n\nLIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n LIMIT 25",
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n LIMIT $limit")]
    public void KnowledgeGraph_AppliesServerBoundToOtherwiseSafeViewerCypher(
        string query,
        string expected) =>
        Assert.Equal(expected, Neo4jGraphStore.EnsureReadOnlyCypher(query));

    [Theory]
    [InlineData("CREATE (n:GraphEntity)")]
    [InlineData("INSERT (:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) DELETE n LIMIT $limit")]
    [InlineData(
        "CALL gds.graph.list() YIELD graphName RETURN graphName LIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId}) RETURN n LIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}), (hidden) RETURN n,hidden LIMIT $limit")]
    [InlineData(
        "MATCH (a:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-->(b:GraphEntity) RETURN a,b LIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n LIMIT $limit; MATCH (m) RETURN m")]
    [InlineData(
        "MATCH (n:GraphNode {projectId: $projectId, graphVersion: $graphVersion}) RETURN n LIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $graphVersion, graphVersion: $projectId}) RETURN n LIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN collect(n) LIMIT $limit")]
    public void KnowledgeGraph_RejectsUnscopedWriteAdminOrUnboundedCypher(string query) =>
        Assert.Throws<InvalidOperationException>(
            () => Neo4jGraphStore.EnsureReadOnlyCypher(query));

    [Fact]
    public void KnowledgeGraph_ViewerDescriptorPublishesGenericFacetsAndCapabilities()
    {
        var schema = new GraphVisualSchemaV3(
            12,
            8,
            [new GraphFacetV3("Code", 12)],
            [new GraphFacetV3("controller", 3)],
            [new GraphFacetV3("CALLS", 8)]);

        Assert.Equal("1.0", schema.ContractVersion);
        Assert.True(schema.Capabilities.Search);
        Assert.Equal(
            ["node-category", "node-role", "edge-type"],
            schema.Facets.Select(facet => facet.Id));
        Assert.Equal("Code", schema.Facets[0].Values[0].Token);
        Assert.Equal("controller", schema.Facets[1].Values[0].Token);
        Assert.Equal("CALLS", schema.Facets[2].Values[0].Token);
    }

    [Fact]
    public void KnowledgeGraph_V3NodeProjectsToGenericViewerFields()
    {
        var node = new GraphVisualNodeV3(
            "node-1", "Code", "controller", "Orders", null, null, null,
            "csharp", 7, new Dictionary<string, object?> { ["role"] = "controller" });

        Assert.Equal("Orders", node.Caption);
        Assert.Equal("Code", node.Category);
        Assert.Equal(["Code"], node.Labels);
        Assert.Equal(7, node.Metrics["degree"]);
    }

    [Fact]
    public void KnowledgeGraph_SerializesOnlyStableViewerContractFields()
    {
        var node = new GraphVisualNodeV3(
            "node-1", "Code", "controller", "Orders", "orders.cs", 1, 9,
            "csharp", 7, new Dictionary<string, object?> { ["role"] = "controller" });
        var schema = new GraphVisualSchemaV3(
            1,
            0,
            [new GraphFacetV3("Code", 1)],
            [new GraphFacetV3("controller", 1)],
            []);

        var json = JsonSerializer.Serialize(
            new { node, schema },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"caption\":\"Orders\"", json);
        Assert.Contains("\"category\":\"Code\"", json);
        Assert.DoesNotContain("\"kind\":", json);
        Assert.DoesNotContain("\"role\":\"controller\",\"name\"", json);
        Assert.DoesNotContain("\"nodeKinds\":", json);
        Assert.DoesNotContain("\"propertyKeys\":", json);
    }

    [Fact]
    public void DatabasePaths_FollowDevelopmentAndProductionRules()
    {
        var repositoryRoot = Path.Combine("D:\\", "Modern Wingman", "Modern Wingman");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(repositoryRoot, "apps", "wingman_dev.db")),
            DatabasePathResolver.GetDevelopmentDatabasePath(repositoryRoot));
        Assert.EndsWith(
            Path.Combine(".Wingman", "sqlite", "wingman.db"),
            DatabasePathResolver.GetProductionDatabasePath(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("manifest-v3", "manifest-v3", true)]
    [InlineData("manifest-v3", null, false)]
    [InlineData("manifest-v3", "older-manifest", false)]
    [InlineData(null, "manifest-v3", false)]
    public void KnowledgeGraph_RequiresMatchingSqliteAndNeo4jManifest(
        string? projectManifest,
        string? activeManifest,
        bool expected) =>
        Assert.Equal(
            expected,
            ProjectEndpoints.HasMatchingGraphManifest(projectManifest, activeManifest));

    [Theory]
    [InlineData(ProjectIndexStatus.PendingChanges, true)]
    [InlineData(ProjectIndexStatus.Stale, true)]
    [InlineData(ProjectIndexStatus.Indexed, false)]
    [InlineData(ProjectIndexStatus.Partial, false)]
    [InlineData(ProjectIndexStatus.Indexing, false)]
    public void ProjectQuestion_RefreshesOnlyPersistentlyStaleIndexStates(
        ProjectIndexStatus status,
        bool expected)
    {
        var project = new ProjectEntity
        {
            Id = "project-question-refresh",
            Name = "Project question refresh",
            RootPath = Path.GetTempPath(),
            IndexStatus = status,
        };

        Assert.Equal(
            expected,
            ConversationEndpoints.RequiresFullIndexRefreshForProjectQuestion(project));
    }
}
