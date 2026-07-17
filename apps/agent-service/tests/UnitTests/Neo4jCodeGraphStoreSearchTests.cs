using AgentService.Infrastructure.CodeGraph;

namespace AgentService.UnitTests;

public sealed class Neo4jCodeGraphStoreSearchTests
{
    [Fact]
    public void BuildLuceneQuery_StructuredPrompt_ProducesLiteralOrTerms()
    {
        const string prompt = "Index freshness: Fresh; manifest: abc (pending)\nPath: A.B → C#";

        var query = Neo4jCodeGraphStore.BuildLuceneQuery(prompt);

        Assert.NotEmpty(query);
        Assert.Contains(" OR ", query);
        Assert.DoesNotContain(":", query);
        Assert.DoesNotContain("(", query);
        Assert.DoesNotContain(")", query);
        Assert.DoesNotContain(";", query);
    }

    [Fact]
    public void BuildLuceneQuery_EscapesLuceneOperatorsInsideIdentifiers()
    {
        var query = Neo4jCodeGraphStore.BuildLuceneQuery("Namespace.Order-Service @token [unsafe]");

        Assert.Contains("Order\\-Service", query);
        Assert.DoesNotContain("[", query);
        Assert.DoesNotContain("]", query);
    }

    [Theory]
    [InlineData("MATCH (n:CodeNodeStage {projectId: $projectId}) RETURN n")]
    [InlineData("MATCH (n:CodeNodeRetired {projectId: $projectId}) RETURN n")]
    [InlineData("MATCH (g:ProjectGraph {projectId: $projectId}) RETURN g")]
    [InlineData("MATCH (p:ProjectGraphPointer {projectId: $projectId}) RETURN p")]
    public void EnsureReadOnlyCypher_RejectsInternalSnapshotStorage(string cypher)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Neo4jCodeGraphStore.EnsureReadOnlyCypher(cypher));
    }
}
