using System.Reflection;
using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>
/// 驗證專案解析 Agent 的資料庫結構/定義唯讀工具（list_database_objects、describe_database_table、
/// get_database_object_definition）。只用暫時的檔案型 SQLite 資料庫測試，SQL Server 分支的參數化查詢
/// 邏輯與 SQLite 分支共用同一套設計，暫無 CI 可用的 SQL Server 執行個體驗證。
/// </summary>
public sealed class ProjectDatabaseToolsRegressionTests
{
    [Fact]
    public async Task ListDatabaseObjectsAsync_應列出實際存在的資料表與檢視表()
    {
        var (tools, dbPath) = await CreateToolsWithSampleDatabaseAsync();
        try
        {
            var result = await tools.ListDatabaseObjectsAsync();

            Assert.Contains(
                result.Objects,
                item => item.ObjectName == "tblInvestmentCategory" && item.Kind == "Table");
            Assert.Contains(
                result.Objects,
                item => item.ObjectName == "vwActiveCategories" && item.Kind == "View");
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task DescribeDatabaseTableAsync_應回傳實際欄位與主鍵()
    {
        var (tools, dbPath) = await CreateToolsWithSampleDatabaseAsync();
        try
        {
            var result = await tools.DescribeDatabaseTableAsync("tblInvestmentCategory");

            Assert.Equal(3, result.Columns.Count);
            Assert.True(result.Columns.Single(column => column.ColumnName == "CategoryId").IsPrimaryKey);
            Assert.False(result.Columns.Single(column => column.ColumnName == "IsDisabled").IsPrimaryKey);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task DescribeDatabaseTableAsync_資料表不存在時_應回傳提示而非例外()
    {
        var (tools, dbPath) = await CreateToolsWithSampleDatabaseAsync();
        try
        {
            var result = await tools.DescribeDatabaseTableAsync("NotExistTable");

            Assert.Empty(result.Columns);
            Assert.NotNull(result.Notice);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task GetDatabaseObjectDefinitionAsync_應回傳檢視表實際部署的定義文字()
    {
        var (tools, dbPath) = await CreateToolsWithSampleDatabaseAsync();
        try
        {
            var result = await tools.GetDatabaseObjectDefinitionAsync("vwActiveCategories");

            Assert.NotNull(result.Definition);
            Assert.Contains("IsDisabled", result.Definition);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task 未設定資料庫連線時_三個資料庫工具都應回傳提示而非例外()
    {
        var tools = new ProjectAnalysisTools(
            "core-regression-project",
            AppContext.BaseDirectory,
            DispatchProxy.Create<IGraphStore, ThrowingGraphStoreProxy>());

        var objects = await tools.ListDatabaseObjectsAsync();
        var table = await tools.DescribeDatabaseTableAsync("anything");
        var definition = await tools.GetDatabaseObjectDefinitionAsync("anything");

        Assert.Empty(objects.Objects);
        Assert.NotNull(objects.Notice);
        Assert.Empty(table.Columns);
        Assert.NotNull(table.Notice);
        Assert.Null(definition.Definition);
        Assert.NotNull(definition.Notice);
    }

    private static async Task<(ProjectAnalysisTools Tools, string DbPath)> CreateToolsWithSampleDatabaseAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wingman-db-tool-test-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tblInvestmentCategory (
                    CategoryId INTEGER NOT NULL PRIMARY KEY,
                    CategoryName TEXT NOT NULL,
                    IsDisabled INTEGER NOT NULL DEFAULT 0
                );
                CREATE VIEW vwActiveCategories AS
                    SELECT CategoryId, CategoryName FROM tblInvestmentCategory WHERE IsDisabled = 0;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var database = new GraphDatabaseSource(
            ProjectDatabaseProvider.Sqlite,
            connectionString,
            Path.GetFileNameWithoutExtension(dbPath));
        var tools = new ProjectAnalysisTools(
            "core-regression-project",
            AppContext.BaseDirectory,
            DispatchProxy.Create<IGraphStore, ThrowingGraphStoreProxy>(),
            database: database);
        return (tools, dbPath);
    }

    private static void DeleteDatabase(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
            File.Delete(dbPath);
    }

    private class ThrowingGraphStoreProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"資料庫工具測試不應呼叫 Graph Store：{targetMethod?.Name}");
    }
}
