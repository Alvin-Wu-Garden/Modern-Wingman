using AgentService.Modules.GraphRAG;
using AgentService.Modules.GraphRAG.ExtractedGraph;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>
/// 驗證 Token Budget Solver（結構鏈路／直接證據字元預算切分）與
/// Context Reranker（證據去重、跨檔案輪流排序）不會退化成「其中一區塊把另一區塊擠光」。
/// </summary>
public sealed class GraphRetrievalServicePromptBudgetTests
{
    [Fact]
    public void ComputeBudgetPlan_一般問題_證據仍保有基準佔比預算()
    {
        var plan = GraphRetrievalService.ComputeBudgetPlan(
            remainingCharacters: 10_000,
            hasExactIdentifier: false,
            evidenceRatio: 0.45,
            evidenceRatioWithExactIdentifier: 0.6);

        Assert.Equal(4_500, plan.EvidenceBudget);
        Assert.Equal(5_500, plan.StructuralBudget);
    }

    [Fact]
    public void ComputeBudgetPlan_精確識別碼問題_證據佔比應高於一般問題()
    {
        var withExactIdentifier = GraphRetrievalService.ComputeBudgetPlan(
            remainingCharacters: 10_000,
            hasExactIdentifier: true,
            evidenceRatio: 0.45,
            evidenceRatioWithExactIdentifier: 0.6);
        var withoutExactIdentifier = GraphRetrievalService.ComputeBudgetPlan(
            remainingCharacters: 10_000,
            hasExactIdentifier: false,
            evidenceRatio: 0.45,
            evidenceRatioWithExactIdentifier: 0.6);

        Assert.True(withExactIdentifier.EvidenceBudget > withoutExactIdentifier.EvidenceBudget);
    }

    [Fact]
    public void ComputeBudgetPlan_剩餘字元數為零_不應回傳負數預算()
    {
        var plan = GraphRetrievalService.ComputeBudgetPlan(
            remainingCharacters: 0,
            hasExactIdentifier: true,
            evidenceRatio: 0.45,
            evidenceRatioWithExactIdentifier: 0.6);

        Assert.Equal(0, plan.StructuralBudget);
        Assert.Equal(0, plan.EvidenceBudget);
    }

    [Fact]
    public void DiversifyEvidenceOrder_相同文字的重複證據_只保留一筆()
    {
        var relationships = new[]
        {
            CreateRelationship("a", "b", "Controller.cs", 10, "呼叫 Service.Process()"),
            CreateRelationship("a", "c", "Controller.cs", 10, "呼叫 Service.Process()"),
        };

        var ordered = GraphRetrievalService.DiversifyEvidenceOrder(relationships);

        Assert.Single(ordered);
    }

    [Fact]
    public void DiversifyEvidenceOrder_多檔案來源_應跨檔案輪流而非單一檔案洗版()
    {
        var relationships = new[]
        {
            CreateRelationship("a", "b", "Controller.cs", 10, "第一筆"),
            CreateRelationship("a", "c", "Controller.cs", 20, "第二筆"),
            CreateRelationship("a", "d", "Controller.cs", 30, "第三筆"),
            CreateRelationship("a", "e", "ServiceDal.cs", 40, "資料庫存取"),
        };

        var ordered = GraphRetrievalService.DiversifyEvidenceOrder(relationships);

        Assert.Equal(4, ordered.Count);
        // ServiceDal.cs 是唯一能跟 Controller.cs 輪流的來源，必須排在第二筆，
        // 不能被 Controller.cs 自己的第二、三筆證據排擠到最後面。
        Assert.Equal("ServiceDal.cs", ordered[1].Properties["sourceFile"]);
    }

    private static GraphRelationship CreateRelationship(
        string sourceKey,
        string targetKey,
        string sourceFile,
        int sourceLine,
        string sourceText) => GraphRelationship.Create(
            GraphRelationshipKind.Calls,
            sourceKey,
            targetKey,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sourceFile"] = sourceFile,
                ["sourceLine"] = sourceLine,
                ["reference"] = sourceText,
            });
}
