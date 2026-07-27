using AgentService.Host.RestEndpoints;
using AgentService.Infrastructure.Persistence;
using AgentService.Modules.GraphRAG;

namespace AgentService.UnitTests;

public sealed class ReadOnlyGraphAndDatabasePathV3Tests
{
    [Theory]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n LIMIT $limit")]
    [InlineData(
        "MATCH (a:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-->(b:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN a,b LIMIT $limit")]
    public void KnowledgeGraph_AllowsScopedBoundedReadOnlyCypher(string query) =>
        Assert.Equal(query, Neo4jGraphStore.EnsureReadOnlyCypher(query));

    [Theory]
    [InlineData("CREATE (n:GraphEntity)")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) DELETE n LIMIT $limit")]
    [InlineData(
        "CALL gds.graph.list() YIELD graphName RETURN graphName LIMIT $limit")]
    [InlineData(
        "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion}) RETURN n")]
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
    public void KnowledgeGraph_RejectsUnscopedWriteAdminOrUnboundedCypher(string query) =>
        Assert.Throws<InvalidOperationException>(
            () => Neo4jGraphStore.EnsureReadOnlyCypher(query));

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
}
