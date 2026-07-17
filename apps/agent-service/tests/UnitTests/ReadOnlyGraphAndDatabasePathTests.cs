using AgentService.Infrastructure.CodeGraph;
using AgentService.Infrastructure.Persistence;

namespace AgentService.UnitTests;

public sealed class ReadOnlyGraphAndDatabasePathTests
{
    [Theory]
    [InlineData("MATCH (n:CodeNode {projectId: $projectId}) RETURN n LIMIT 10")]
    [InlineData("RETURN 1")]
    [InlineData("WITH 1 AS value RETURN value")]
    [InlineData("UNWIND [1,2] AS value RETURN value")]
    public void KnowledgeGraph_AllowsReadOnlyCypher(string query)
    {
        Assert.Equal(query, Neo4jCodeGraphStore.EnsureReadOnlyCypher(query));
    }

    [Theory]
    [InlineData("CREATE (n:Unsafe)")]
    [InlineData("MATCH (n) DELETE n")]
    [InlineData("CALL apoc.create.node([], {})")]
    [InlineData("CALL gds.graph.list()")]
    [InlineData("MATCH (n) RETURN n; MATCH (m) RETURN m")]
    [InlineData("MATCH (n) RETURN n LIMIT 10")]
    [InlineData("MATCH ()-->() RETURN 1")]
    [InlineData("MATCH (n:CodeNode {projectId: $projectId}), (hidden) RETURN hidden")]
    [InlineData("MATCH (n:ProjectGraph {projectId: $projectId}) RETURN n")]
    [InlineData("MATCH (n:CodeNode) WITH n, $projectId AS ignored RETURN n")]
    [InlineData("MATCH (a:CodeNode {projectId: $projectId})-->(b:CodeNode) RETURN a, b")]
    [InlineData("CALL db.index.fulltext.queryNodes('codeSearch', 'secret') YIELD node WITH node, $projectId AS ignored RETURN node")]
    public void KnowledgeGraph_RejectsWriteAdminAndMultipleStatements(string query)
    {
        Assert.Throws<InvalidOperationException>(() => Neo4jCodeGraphStore.EnsureReadOnlyCypher(query));
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
}
