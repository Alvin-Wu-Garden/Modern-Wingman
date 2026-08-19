using AgentService.Application.Models;
using AgentService.Modules.GraphRAG;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>驗證完整索引只會發布抽取前後內容一致的原始碼快照。</summary>
public sealed class GraphIndexingConsistencyRegressionTests
{
    [Fact]
    public void 抽取前後檔案快照相同時_應通過一致性檢查()
    {
        ProjectIndexedFile[] snapshot =
        [
            new("Controllers/TradeController.cs", "abc"),
            new("Services/TradeService.cs", "def"),
        ];

        GraphIndexingService.EnsureFileSnapshotUnchanged(snapshot, snapshot.Reverse().ToArray());
    }

    [Fact]
    public void 抽取期間檔案內容變更時_應拒絕發布()
    {
        ProjectIndexedFile[] before = [new("Controllers/TradeController.cs", "old")];
        ProjectIndexedFile[] after = [new("Controllers/TradeController.cs", "new")];

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GraphIndexingService.EnsureFileSnapshotUnchanged(before, after));

        Assert.Contains("索引期間偵測到原始碼變更", exception.Message, StringComparison.Ordinal);
    }
}
