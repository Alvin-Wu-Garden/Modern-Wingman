using AgentService.Modules.GraphRAG;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>
/// 驗證數字功能代號問題（例如「140078-股權交易作業的主要資料流程是什麼?」）
/// 會被 Query Plan 拆成一個獨立、最高優先序的精確識別碼查詢，
/// 而不是被整句話的模糊比對稀釋掉。
/// </summary>
public sealed class GraphRetrievalServiceQueryPlanTests
{
    [Fact]
    public void BuildQueryPlan_數字功能代號問題_應標記精確識別碼且排最高優先()
    {
        var plan = GraphRetrievalService.BuildQueryPlan(
            "140078-股權交易作業的主要資料流程是什麼?");

        Assert.True(plan.HasExactIdentifier);
        Assert.Contains(plan.Queries, query => query.Text == "140078");
        var topQuery = plan.Queries
            .OrderByDescending(query => query.Priority)
            .First();
        Assert.Equal("140078", topQuery.Text);
    }

    [Fact]
    public void BuildQueryPlan_純中文問題_不應標記精確識別碼()
    {
        var plan = GraphRetrievalService.BuildQueryPlan(
            "股權交易作業的主要資料流程是什麼?");

        Assert.False(plan.HasExactIdentifier);
    }
}
