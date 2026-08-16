using System.Reflection;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Orchestration;
using AgentService.Modules.GraphRAG;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>
/// 端對端驗證：專案解析對話準備階段真的會把「專案設定的資料庫連線來源」傳進資料庫工具，
/// 而不是讓 list_database_objects 這類工具永遠因為 database 是 null 而回報「尚未設定資料庫連線」。
/// </summary>
public sealed class ProjectConversationPreparationServiceDatabaseWiringTests
{
    [Fact]
    public async Task PrepareAsync_專案已設定資料庫連線時_list_database_objects_應真的查詢而非回報未設定()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wingman-prep-wiring-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE tblWiringSample (Id INTEGER NOT NULL PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            var databaseSource = new GraphDatabaseSource(
                ProjectDatabaseProvider.Sqlite,
                connectionString,
                Path.GetFileNameWithoutExtension(dbPath));
            var graphStore = DispatchProxy.Create<IGraphStore, PingFalseGraphStoreProxy>();
            var service = new ProjectConversationPreparationService(
                new GraphRetrievalService(
                    graphStore,
                    Options.Create(new GraphRetrievalOptions()),
                    NullLogger<GraphRetrievalService>.Instance),
                graphStore,
                new FixedGraphDatabaseSourceProvider(databaseSource),
                NullLogger<ProjectConversationPreparationService>.Instance,
                NullLogger<ProjectAnalysisTools>.Instance);

            var project = new ProjectEntity
            {
                Id = "wiring-test-project",
                Name = "Wiring Test",
                RootPath = Path.GetTempPath(),
            };
            var profile = new ModelProviderProfile { Id = "test-profile", DisplayName = "Test" };
            var activity = new AgentActivityReporter("wiring-test-run", _ => Task.CompletedTask);

            var preparation = await service.PrepareAsync(
                project, "測試問題", profile, "test-model", activity, CancellationToken.None);

            var tool = preparation.Tools.Single(function => function.Name == "list_database_objects");
            // AIFunctionFactory 產生的封裝委派不保留底層方法的預設參數值，呼叫時必須明確給全部參數。
            var arguments = new AIFunctionArguments
            {
                ["nameFilter"] = null,
                ["kind"] = null,
                ["maxResults"] = 100,
            };
            var result = await tool.InvokeAsync(arguments, CancellationToken.None);

            var resultText = result?.ToString() ?? string.Empty;
            Assert.DoesNotContain("尚未設定資料庫連線", resultText);
            Assert.Contains("tblWiringSample", resultText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private sealed class FixedGraphDatabaseSourceProvider(GraphDatabaseSource source)
        : IGraphDatabaseSourceProvider
    {
        public Task<GraphDatabaseSource?> GetAsync(
            ProjectEntity project, CancellationToken cancellationToken = default) =>
            Task.FromResult<GraphDatabaseSource?>(source);

        public Task<IReadOnlyList<GraphDatabaseSource>> GetAllAsync(
            ProjectEntity project, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphDatabaseSource>>([source]);
    }

    /// <summary>只有 PingAsync 回傳 false，讓 Graph 探測快速判定為 unavailable，不觸碰真正的 Neo4j 呼叫。</summary>
    private class PingFalseGraphStoreProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IGraphStore.PingAsync)
                ? Task.FromResult(false)
                : throw new InvalidOperationException(
                    $"測試不預期呼叫 Graph Store：{targetMethod?.Name}");
    }
}
