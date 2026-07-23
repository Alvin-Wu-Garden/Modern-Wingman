using AgentService.Infrastructure.CodeGraph;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AgentService.UnitTests;

public sealed class Neo4jLifecycleLockTests
{
    [Fact]
    public void OfflinePackageGuidance_UsesDefaultPackageDirectoryWhenNotConfigured()
    {
        var options = new Neo4jLifecycleOptions { OfflinePackageDir = null };

        var directory = Neo4jLifecycleService.GetEffectiveOfflinePackageDir(options);
        var guidance = Neo4jLifecycleService.GetOfflinePackageGuidance(options);

        Assert.EndsWith(Path.Combine(".wingman", "neo4j", "packages"), directory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(directory, guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("neo4j-community*.zip", guidance, StringComparison.Ordinal);
        Assert.Contains("jre*.zip", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossProcessFileLock_CanCrossAwaitAndIsReleasedByDispose()
    {
        await using (var first = await Neo4jLifecycleService.AcquireCrossProcessLockAsync(CancellationToken.None))
        {
            await Task.Yield();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Neo4jLifecycleService.AcquireCrossProcessLockAsync(timeout.Token));
        }

        await using var acquiredAfterRelease =
            await Neo4jLifecycleService.AcquireCrossProcessLockAsync(CancellationToken.None);
        Assert.True(acquiredAfterRelease.CanWrite);
    }

    [Fact]
    public async Task ManagedMode_RejectsPortOwnedByAnotherProcess_EvenWhenItLooksLikeNeo4j()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var lifecycle = new Neo4jLifecycleService(
            Options.Create(new Neo4jLifecycleOptions { Mode = "managed" }),
            Options.Create(new Neo4jOptions { Uri = $"bolt://127.0.0.1:{port}" }),
            new MemoryGraphStore(null),
            new NoopHttpClientFactory(),
            NullLogger<Neo4jLifecycleService>.Instance);

        var available = await lifecycle.EnsureAvailableAsync();

        Assert.False(available);
        Assert.Equal("port-conflict", lifecycle.Status);
        Assert.Contains("避免誤用 Docker", lifecycle.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalMode_StillAcceptsExplicitlyConfiguredReachableNeo4j()
    {
        await using var lifecycle = new Neo4jLifecycleService(
            Options.Create(new Neo4jLifecycleOptions { Mode = "external" }),
            Options.Create(new Neo4jOptions { Uri = "bolt://127.0.0.1:7687" }),
            new MemoryGraphStore(null),
            new NoopHttpClientFactory(),
            NullLogger<Neo4jLifecycleService>.Instance);

        Assert.True(await lifecycle.EnsureAvailableAsync());
        Assert.Equal("running", lifecycle.Status);
        Assert.Null(lifecycle.LastError);
    }

    [Fact]
    public async Task PingAsync_BlackHoleEndpoint_ReturnsWithinConfiguredTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var store = new Neo4jCodeGraphStore(
            Options.Create(new Neo4jOptions
            {
                Uri = $"bolt://127.0.0.1:{port}",
                ConnectionTimeoutSeconds = 1,
            }),
            NullLogger<Neo4jCodeGraphStore>.Instance);
        var stopwatch = Stopwatch.StartNew();

        var available = await store.PingAsync();

        Assert.False(available);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Ping took {stopwatch.Elapsed} instead of honoring its timeout.");
    }

    [Fact]
    public async Task PingAsync_PropagatesCallerCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var store = new Neo4jCodeGraphStore(
            Options.Create(new Neo4jOptions
            {
                Uri = $"bolt://127.0.0.1:{port}",
                ConnectionTimeoutSeconds = 10,
            }),
            NullLogger<Neo4jCodeGraphStore>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.PingAsync(cancellation.Token));
    }
}
