using System.Reflection;
using AgentService.Modules.GraphRAG;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>
/// 驗證專案原始碼工具的兩個核心安全與效能邊界。
/// 測試只在工作區 temp 建立短生命週期資料，並於 finally 中清除，避免污染人工測試環境。
/// </summary>
public sealed class ProjectAnalysisToolsRegressionTests
{
    [Fact]
    public async Task ReadFileRangeAsync_超過兩千行時_應限制回傳範圍()
    {
        var root = CreateTestRoot();
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(root, "large.txt"),
                Enumerable.Range(1, 2_005).Select(number => $"line-{number}"));

            var tools = CreateTools(root);
            var result = await tools.ReadFileRangeAsync("large.txt", 1, 9_999);

            Assert.Equal(2_000, result.Lines.Count);
            Assert.Equal(2_000, result.Lines[^1].Line);
            Assert.True(result.HasMore);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task ReadFileRangeAsync_相對路徑跳出專案根目錄時_應拒絕讀取()
    {
        var root = CreateTestRoot();
        try
        {
            var tools = CreateTools(root);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                tools.ReadFileRangeAsync("../outside.txt", 1, 10));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static ProjectAnalysisTools CreateTools(string root) =>
        new(
            "core-regression-project",
            root,
            DispatchProxy.Create<IGraphStore, ThrowingGraphStoreProxy>());

    private static string CreateTestRoot()
    {
        var workspace = FindWorkspaceRoot();
        var temp = Path.Combine(workspace, "temp");
        Directory.CreateDirectory(temp);
        var root = Path.Combine(temp, $"core-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !(Directory.Exists(Path.Combine(directory.FullName, "apps")) &&
                 Directory.Exists(Path.Combine(directory.FullName, "docs")) &&
                 File.Exists(Path.Combine(directory.FullName, "package.json"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到 Modern Wingman 工作區根目錄。");
    }

    private static void DeleteTestRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var expectedTemp = Path.Combine(FindWorkspaceRoot(), "temp") +
                           Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(expectedTemp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒絕清理工作區 temp 以外的測試目錄。");

        if (Directory.Exists(fullRoot))
            Directory.Delete(fullRoot, recursive: true);
    }

    private class ThrowingGraphStoreProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"原始碼邊界測試不應呼叫 Graph Store：{targetMethod?.Name}");
    }
}
