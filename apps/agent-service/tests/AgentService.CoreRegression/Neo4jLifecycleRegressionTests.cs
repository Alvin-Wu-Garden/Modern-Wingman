using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>驗證 Neo4j readiness 探測不會在正常冷啟動期間製造重複失敗 Log。</summary>
public sealed class Neo4jLifecycleRegressionTests
{
    [Fact]
    public void RuntimeOptions_正式環境預設不保留ManagedProcess()
    {
        Assert.False(new GraphRagNeo4jRuntimeOptions()
            .PreserveManagedProcessOnShutdown);
    }

    [Fact]
    public async Task PingAsync_連線尚未就緒時_只回傳False且不逐次記錄失敗()
    {
        var unavailablePort = ReserveUnusedLoopbackPort();
        var logger = new CollectingLogger<Neo4jGraphStore>();
        var manifests = DispatchProxy.Create<
            IProjectIndexManifestStore,
            UnusedManifestStoreProxy>();
        await using var store = new Neo4jGraphStore(
            Options.Create(new GraphRagNeo4jOptions
            {
                Uri = $"bolt://127.0.0.1:{unavailablePort}",
                Username = "neo4j",
                Password = "regression-only",
                Database = "neo4j",
                ConnectionTimeoutSeconds = 1,
                TransactionRetrySeconds = 1,
                WriteBatchSize = 100,
            }),
            Options.Create(new GraphRagNeo4jRuntimeOptions { Mode = "external" }),
            manifests,
            logger);

        Assert.False(await store.PingAsync());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task DeleteProjectAsync_Neo4j未啟用時_不得假裝刪除成功()
    {
        var manifests = DispatchProxy.Create<
            IProjectIndexManifestStore,
            UnusedManifestStoreProxy>();
        await using var store = new Neo4jGraphStore(
            Options.Create(new GraphRagNeo4jOptions
            {
                Disabled = true,
                Password = "regression-only",
            }),
            Options.Create(new GraphRagNeo4jRuntimeOptions { Mode = "disabled" }),
            manifests,
            new CollectingLogger<Neo4jGraphStore>());

        var exception = await Assert.ThrowsAsync<GraphStoreException>(
            () => store.DeleteProjectAsync("project-1"));

        Assert.Equal(GraphStoreFailureKind.Unavailable, exception.FailureKind);
    }

    private static int ReserveUnusedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private class UnusedManifestStoreProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"此測試不應呼叫 manifest store：{targetMethod?.Name ?? "unknown"}。");
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
