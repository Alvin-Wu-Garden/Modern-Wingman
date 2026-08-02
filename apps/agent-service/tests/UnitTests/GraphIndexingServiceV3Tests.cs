using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Modules.GraphRAG;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentService.UnitTests;

public sealed class GraphIndexingServiceV3Tests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"wingman-index-v3-{Guid.NewGuid():N}");
    private bool _managedNeo4jUsed;

    public GraphIndexingServiceV3Tests() => Directory.CreateDirectory(_root);

    /// <summary>大型專案裁剪必須保留登入與認證模組，否則登入流程只會剩 Controller 外殼。</summary>
    [Theory]
    [InlineData("LoginAndPassword/LoginAndPassword.cs")]
    [InlineData("Security/AuthenticationService.cs")]
    [InlineData("Account/PasswordValidator.cs")]
    public void LargeRepositoryCallPath_KeepsAuthenticationFiles(string relativePath)
    {
        var path = Path.Combine(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(CSharpGraphExtractor.IsLargeRepositoryCallPathFile(_root, path));
    }

    [Fact]
    public async Task ManagedNeo4jRuntime_RejectsOccupiedPortWithoutMatchingOwnership()
    {
        // 使用 OS 配發的暫時連接埠，驗證沒有 Wingman ownership 時即使 store
        // test double 可回應，managed runtime 也不得把任意 listener 當成內建 Neo4j。
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var neo4j = Options.Create(new GraphRagNeo4jOptions
        {
            Uri = $"bolt://127.0.0.1:{port}",
            Username = "neo4j",
            Password = "unit-test-only",
            Database = "neo4j",
        });
        var runtimeOptions = Options.Create(
            new GraphRagNeo4jRuntimeOptions { Mode = "managed" });
        await using var runtime = new Neo4jRuntime(
            runtimeOptions,
            neo4j,
            new InMemoryGraphStore(),
            new DefaultHttpClientFactory(),
            NullLogger<Neo4jRuntime>.Instance);

        Assert.False(await runtime.EnsureAvailableAsync());
        Assert.Equal("port-conflict", runtime.Status);
        Assert.Contains("managed 模式已拒絕啟動", runtime.LastError);
    }

    [Fact]
    public async Task IndexProjectAsync_PublishesFullThenUsesVerifiedNoOp()
    {
        var source = Path.Combine(_root, "Order.cs");
        await File.WriteAllTextAsync(source, "public sealed class Order { }");
        var fixture = CreateFixture();

        var first = await fixture.Service.IndexProjectAsync("project");
        var firstManifest = first.IndexManifestVersion;
        var second = await fixture.Service.IndexProjectAsync("project");

        Assert.Equal(1, fixture.Store.PublishCount);
        Assert.NotNull(firstManifest);
        Assert.Equal(firstManifest, second.IndexManifestVersion);
        Assert.Equal(ProjectIndexStatus.Indexed, second.IndexStatus);
        Assert.Equal("no-op", fixture.Service.GetLastRun("project")!.Mode);
        Assert.Equal("3.0", fixture.Manifests.Current!.GraphSchemaVersion);
    }

    /// <summary>
    /// Extractor 規則升版必須使 no-op 失效；否則來源未變時仍會沿用舊解析結果。
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_ExtractorVersionChangeInvalidatesNoOp()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Order.cs"),
            "public sealed class Order { }");
        var fixture = CreateFixture();
        await fixture.Service.IndexProjectAsync("project");
        await fixture.Service.IndexProjectAsync("project");
        Assert.Equal("no-op", fixture.Service.GetLastRun("project")!.Mode);

        fixture.Extractor.Version = "2.0.0";
        await fixture.Service.IndexProjectAsync("project");

        Assert.Equal("full", fixture.Service.GetLastRun("project")!.Mode);
        Assert.Equal(2, fixture.Store.PublishCount);
        Assert.Contains(
            "fixture-v3=2.0.0",
            fixture.Manifests.Current!.IndexerVersion,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndexProjectAsync_ReusesAiCommunitySummaryOnlyWhenCacheKeyMatches()
    {
        var source = Path.Combine(_root, "Order.cs");
        await File.WriteAllTextAsync(source, "public sealed class Order { }");
        var fixture = CreateFixture();
        await fixture.Service.IndexProjectAsync("project");
        var generated = Assert.Single(fixture.Store.CommunityReports);
        fixture.Store.ReplaceCommunityReports(
        [
            generated with
            {
                Summary = "已驗證的 AI 社群摘要",
                AiEnriched = true,
            },
        ]);

        await File.AppendAllTextAsync(source, "\n// implementation-only change");
        await fixture.Service.IndexProjectAsync("project");

        var reused = Assert.Single(fixture.Store.CommunityReports);
        Assert.Equal(generated.CacheKey, reused.CacheKey);
        Assert.Equal("已驗證的 AI 社群摘要", reused.Summary);
        Assert.True(reused.AiEnriched);
    }

    [Fact]
    public async Task IndexProjectAsync_ExcludesTestArtifactsFromExtractorAndManifest()
    {
        var source = Path.Combine(_root, "Order.cs");
        var tests = Path.Combine(_root, "tests");
        Directory.CreateDirectory(tests);
        var testFile = Path.Combine(tests, "OrderTests.cs");
        await File.WriteAllTextAsync(source, "public sealed class Order { }");
        await File.WriteAllTextAsync(testFile, "public sealed class OrderTests { }");
        var fixture = CreateFixture();

        await fixture.Service.IndexProjectAsync("project");

        Assert.Single(fixture.Extractor.LastFiles);
        Assert.Equal(source, fixture.Extractor.LastFiles[0], ignoreCase: true);
        Assert.DoesNotContain(
            fixture.Manifests.Current!.Files,
            file => file.RelativePath.Contains("tests", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IndexProjectAsync_PublishFailurePreservesPreviousSuccessfulState()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Order.cs"), "public sealed class Order { }");
        var fixture = CreateFixture();
        var first = await fixture.Service.IndexProjectAsync("project");
        var previousManifest = first.IndexManifestVersion;
        var previousNodes = first.NodeCount;
        await File.AppendAllTextAsync(Path.Combine(_root, "Order.cs"), "\n// change");
        fixture.Store.FailPublish = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.IndexProjectAsync("project"));

        Assert.Equal("simulated publish failure", error.Message);
        var saved = await fixture.Projects.GetAsync("project");
        Assert.Equal(previousManifest, saved!.IndexManifestVersion);
        Assert.Equal(previousNodes, saved.NodeCount);
        Assert.Equal(ProjectIndexStatus.Stale, saved.IndexStatus);
        Assert.Equal(IndexManifestStatus.Failed, fixture.Manifests.Latest!.Status);
        Assert.DoesNotContain("simulated", saved.IndexError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndexProjectAsync_DatabasePreflightFailurePreservesPreviousSuccessfulGraph()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Order.cs"),
            "public sealed class Order { }");
        var databaseSources = new MutableDatabaseSourceProvider();
        var fixture = CreateFixture(databaseSources);
        var first = await fixture.Service.IndexProjectAsync("project");
        var previousManifest = first.IndexManifestVersion;
        var previousPublishCount = fixture.Store.PublishCount;
        databaseSources.Source = new GraphDatabaseSource(
            ProjectDatabaseProvider.Sqlite,
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "missing.db"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString,
            "missing");

        await Assert.ThrowsAnyAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => fixture.Service.IndexProjectAsync("project"));

        var saved = await fixture.Projects.GetAsync("project");
        Assert.Equal(previousManifest, saved!.IndexManifestVersion);
        Assert.Equal(previousPublishCount, fixture.Store.PublishCount);
        Assert.Equal(previousManifest, fixture.Store.ActiveManifest);
        Assert.Equal(ProjectIndexStatus.Stale, saved.IndexStatus);
        Assert.Contains("資料庫唯讀連線失敗", saved.IndexError);
    }

    [Fact]
    public async Task IndexProjectAsync_DatabaseConfigurationFingerprintInvalidatesNoOp()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Order.cs"),
            "public sealed class Order { }");
        var databasePath = Path.Combine(_root, "configuration-fingerprint.db");
        await CreateSqliteSchemaAsync(databasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;
        var databaseSources = new MutableDatabaseSourceProvider
        {
            Source = new GraphDatabaseSource(
                ProjectDatabaseProvider.Sqlite,
                connectionString,
                "configuration-fingerprint",
                "configuration-v1"),
        };
        var fixture = CreateCSharpFixture(databaseSources);

        await fixture.Service.IndexProjectAsync("project");
        var firstFingerprint = fixture.Store.LastSnapshot!.WorkingTreeFingerprint;
        await fixture.Service.IndexProjectAsync("project");
        Assert.Equal("no-op", fixture.Service.GetLastRun("project")!.Mode);
        Assert.Equal(1, fixture.Store.PublishCount);

        databaseSources.Source = databaseSources.Source with
        {
            ConfigurationFingerprint = "configuration-v2",
        };
        await fixture.Service.IndexProjectAsync("project");

        Assert.Equal("full", fixture.Service.GetLastRun("project")!.Mode);
        Assert.Equal(2, fixture.Store.PublishCount);
        Assert.NotEqual(
            firstFingerprint,
            fixture.Store.LastSnapshot!.WorkingTreeFingerprint);
    }

    [Fact]
    public async Task IndexProjectAsync_ConfiguredDatabaseWithoutFeaturePublishesActionableWarning()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Order.cs"),
            "public sealed class Order { }");
        var databasePath = Path.Combine(_root, "no-business-feature.db");
        await CreateSqliteSchemaAsync(databasePath);
        var databaseSources = new MutableDatabaseSourceProvider
        {
            Source = new GraphDatabaseSource(
                ProjectDatabaseProvider.Sqlite,
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ConnectionString,
                "no-business-feature",
                "configuration-v1"),
        };
        var fixture = CreateCSharpFixture(databaseSources);

        var project = await fixture.Service.IndexProjectAsync("project");

        Assert.Equal(ProjectIndexStatus.Partial, project.IndexStatus);
        Assert.Contains("沒有產生任何 Business Feature", project.IndexError);
        var warning = Assert.Single(
            fixture.Store.LastSnapshot!.Diagnostics,
            item => item.Code == "DATABASE_FEATURES_MISSING");
        Assert.Equal(GraphDiagnosticSeverity.Warning, warning.Severity);
        Assert.True(warning.Retryable);
    }

    [Fact]
    public async Task IncrementalIndexAsync_DatabaseSettingChangeRunsFingerprintWhenSourceIsUnchanged()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Order.cs"),
            "public sealed class Order { }");
        var databasePath = Path.Combine(_root, "incremental-setting-change.db");
        await CreateSqliteSchemaAsync(databasePath);
        var databaseSources = new MutableDatabaseSourceProvider();
        var fixture = CreateCSharpFixture(databaseSources);
        await fixture.Service.IndexProjectAsync("project");
        Assert.Equal(1, fixture.Store.PublishCount);

        databaseSources.Source = new GraphDatabaseSource(
            ProjectDatabaseProvider.Sqlite,
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ConnectionString,
            "incremental-setting-change",
            "configuration-v1");
        await fixture.Service.MarkPendingChangesAsync("project");

        var refreshed = await fixture.Service.IncrementalIndexAsync("project");

        Assert.NotNull(refreshed);
        Assert.Equal("full", fixture.Service.GetLastRun("project")!.Mode);
        Assert.Equal(2, fixture.Store.PublishCount);
        Assert.Contains(
            fixture.Store.LastSnapshot!.Artifacts,
            artifact => artifact.Kind == "database" &&
                        artifact.Status == "indexed");
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ReconcilesNeo4jPublishAfterSqlitePromoteFailure()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "Order.cs"),
            "public sealed class Order { }");
        var fixture = CreateFixture();
        fixture.Manifests.FailNextPromote = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.IndexProjectAsync("project"));

        Assert.Equal("simulated manifest promote failure", error.Message);
        Assert.NotNull(fixture.Store.ActiveManifest);
        Assert.Null(fixture.Manifests.Current);
        Assert.Equal(IndexManifestStatus.Fresh, fixture.Manifests.Latest!.Status);

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("project");
        var project = await fixture.Projects.GetAsync("project");

        Assert.Equal(fixture.Store.ActiveManifest, diagnostics.Current!.Version);
        Assert.Equal(ProjectIndexStatus.Indexed, project!.IndexStatus);
        Assert.Equal(diagnostics.Current.Version, project.IndexManifestVersion);
        Assert.Null(project.IndexError);
    }

    [Fact]
    public async Task IndexProjectAsync_CancellationPreservesPreviousActiveGraph()
    {
        var source = Path.Combine(_root, "Order.cs");
        await File.WriteAllTextAsync(source, "public sealed class Order { }");
        var fixture = CreateFixture();
        var first = await fixture.Service.IndexProjectAsync("project");
        var previousManifest = first.IndexManifestVersion;
        var previousDigest = fixture.Store.LastSnapshot!.CanonicalDigest;
        await File.AppendAllTextAsync(source, "\n// changed");
        fixture.Store.CancelPublish = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.IndexProjectAsync("project"));

        var saved = await fixture.Projects.GetAsync("project");
        Assert.Equal(previousManifest, fixture.Store.ActiveManifest);
        Assert.Equal(previousManifest, saved!.IndexManifestVersion);
        Assert.Equal(previousDigest, fixture.Store.LastSnapshot!.CanonicalDigest);
        Assert.Equal(ProjectIndexStatus.Stale, saved.IndexStatus);
        Assert.Equal(IndexManifestStatus.Failed, fixture.Manifests.Latest!.Status);
    }

    [Fact]
    public async Task CatchUpAsync_UsesContentHashEvenWhenTimestampIsRestored()
    {
        var path = Path.Combine(_root, "Order.cs");
        await File.WriteAllTextAsync(path, "public sealed class Order { }");
        var fixture = CreateFixture();
        await fixture.Service.IndexProjectAsync("project");
        var originalTimestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "public sealed class Changed { }");
        File.SetLastWriteTimeUtc(path, originalTimestamp);

        var changed = await fixture.Service.CatchUpAsync("project");

        Assert.True(changed);
        var diagnostics = await fixture.Service.GetDiagnosticsAsync("project");
        Assert.Contains(
            diagnostics.PendingFiles,
            value => value.Equals("order.cs", StringComparison.OrdinalIgnoreCase));
        var project = await fixture.Projects.GetAsync("project");
        Assert.Equal(ProjectIndexStatus.PendingChanges, project!.IndexStatus);
    }

    [Fact]
    public async Task IndexProjectAsync_CSharpBodyOnlyUsesDeltaAndMatchesCleanFullDigest()
    {
        var path = Path.Combine(_root, "OrderService.cs");
        await File.WriteAllTextAsync(
            path,
            "public sealed class OrderService { public int Calculate() { return 1; } }");
        var deltaFixture = CreateCSharpFixture();
        await deltaFixture.Service.IndexProjectAsync("project");

        await File.WriteAllTextAsync(
            path,
            "public sealed class OrderService { public int Calculate() { return 2; } }");
        await deltaFixture.Service.IndexProjectAsync("project");
        var deltaSnapshot = deltaFixture.Store.LastSnapshot!;

        Assert.Equal("body-delta", deltaFixture.Service.GetLastRun("project")!.Mode);
        Assert.Equal("body-delta", deltaSnapshot.Mode);

        var cleanFullFixture = CreateCSharpFixture();
        await cleanFullFixture.Service.IndexProjectAsync("project");
        var fullSnapshot = cleanFullFixture.Store.LastSnapshot!;

        Assert.Equal("full", fullSnapshot.Mode);
        Assert.Equal(fullSnapshot.CanonicalDigest, deltaSnapshot.CanonicalDigest);
        Assert.Equal(
            fullSnapshot.Nodes.Select(node => node.Id),
            deltaSnapshot.Nodes.Select(node => node.Id));
        Assert.Equal(
            fullSnapshot.Edges.Select(edge => edge.Id),
            deltaSnapshot.Edges.Select(edge => edge.Id));
    }

    [Fact]
    public async Task IndexProjectAsync_BodyDeltaRecomputesEmbeddedSqlDataEdges()
    {
        var path = Path.Combine(_root, "OrderRepository.cs");
        await File.WriteAllTextAsync(
            path,
            """
            public sealed class OrderRepository
            {
                public object Load(dynamic db) =>
                    db.Query("SELECT ID FROM dbo.tblOrders");
            }
            """);
        var deltaFixture = CreateCSharpAndSqlFixture();
        await deltaFixture.Service.IndexProjectAsync("project");

        await File.WriteAllTextAsync(
            path,
            """
            public sealed class OrderRepository
            {
                public object Load(dynamic db) =>
                    db.Query("SELECT ID FROM dbo.tblArchivedOrders");
            }
            """);
        await deltaFixture.Service.IndexProjectAsync("project");
        var deltaSnapshot = deltaFixture.Store.LastSnapshot!;

        Assert.Equal("body-delta", deltaFixture.Service.GetLastRun("project")!.Mode);
        Assert.Contains(deltaSnapshot.Nodes, node =>
            node.Id.EndsWith("/table/tblarchivedorders", StringComparison.Ordinal));
        Assert.DoesNotContain(deltaSnapshot.Nodes, node =>
            node.Id.EndsWith("/table/tblorders", StringComparison.Ordinal));

        var fullFixture = CreateCSharpAndSqlFixture();
        await fullFixture.Service.IndexProjectAsync("project");
        Assert.Equal(
            fullFixture.Store.LastSnapshot!.CanonicalDigest,
            deltaSnapshot.CanonicalDigest);
    }

    [Fact]
    public async Task IncrementalIndexAsync_MeetsOneAndTwentyFiveFileGatesAndMatchesCleanFull()
    {
        var paths = new List<string>();
        for (var index = 0; index < 25; index++)
        {
            var path = Path.Combine(_root, $"Service{index:D2}.cs");
            paths.Add(path);
            await File.WriteAllTextAsync(
                path,
                $"namespace Acme; public sealed class Service{index:D2} " +
                $"{{ public int Run() {{ return {index}; }} }}");
        }
        var delta = CreateCSharpFixture();
        await delta.Service.IndexProjectAsync("project");

        await File.WriteAllTextAsync(
            paths[0],
            "namespace Acme; public sealed class Service00 " +
            "{ public int Run() { return 100; } }");
        var oneFileClock = Stopwatch.StartNew();
        await delta.Service.IncrementalIndexAsync("project");
        oneFileClock.Stop();
        Assert.Equal("body-delta", delta.Service.GetLastRun("project")!.Mode);
        Assert.True(oneFileClock.Elapsed < TimeSpan.FromSeconds(10),
            $"1-file body delta 耗時 {oneFileClock.Elapsed.TotalMilliseconds:F0}ms。");
        var oneFileShadow = CreateCSharpFixture();
        await oneFileShadow.Service.IndexProjectAsync("project");
        Assert.Equal(
            oneFileShadow.Store.LastSnapshot!.CanonicalDigest,
            delta.Store.LastSnapshot!.CanonicalDigest);

        for (var index = 0; index < paths.Count; index++)
        {
            await File.WriteAllTextAsync(
                paths[index],
                $"namespace Acme; public sealed class Service{index:D2} " +
                $"{{ public int Run() {{ return {index + 200}; }} }}");
        }
        var mediumClock = Stopwatch.StartNew();
        await delta.Service.IncrementalIndexAsync("project");
        mediumClock.Stop();
        Assert.Equal("body-delta", delta.Service.GetLastRun("project")!.Mode);
        Assert.True(mediumClock.Elapsed < TimeSpan.FromSeconds(30),
            $"25-file body delta 耗時 {mediumClock.Elapsed.TotalMilliseconds:F0}ms。");
        var mediumShadow = CreateCSharpFixture();
        await mediumShadow.Service.IndexProjectAsync("project");
        Assert.Equal(
            mediumShadow.Store.LastSnapshot!.CanonicalDigest,
            delta.Store.LastSnapshot!.CanonicalDigest);

        var noChangeClock = Stopwatch.StartNew();
        Assert.Null(await delta.Service.IncrementalIndexAsync("project"));
        noChangeClock.Stop();
        Assert.True(noChangeClock.Elapsed < TimeSpan.FromSeconds(2),
            $"無變更偵測耗時 {noChangeClock.Elapsed.TotalMilliseconds:F0}ms。");

        await File.WriteAllTextAsync(
            paths[0],
            "namespace Acme; public sealed class RenamedService00 " +
            "{ public int Run() { return 200; } }");
        await delta.Service.IncrementalIndexAsync("project");
        Assert.Equal("full", delta.Service.GetLastRun("project")!.Mode);
    }

    [FblGraphAcceptanceFact]
    [Trait("Category", "ExternalAcceptance")]
    public async Task RealFblDatabaseFingerprint_IsStableAcrossImmediateReads()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("WINGMAN_FBL_ACCEPTANCE_SQLSERVER")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_ACCEPTANCE_SQLSERVER。");
        var extractor = new SqlServerGraphExtractor(
            NullLogger<SqlServerGraphExtractor>.Instance);
        var source = new SqlServerGraphSource(connectionString, "FBL_SPV_SIT");

        var first = await extractor.ComputeDatabaseFingerprintAsync(source);
        var second = await extractor.ComputeDatabaseFingerprintAsync(source);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
    }

    [FblNeo4jAcceptanceFact]
    [Trait("Category", "ExternalAcceptance")]
    public async Task ManagedNeo4jRuntime_StartsAndCreatesV3Schema()
    {
        _managedNeo4jUsed = true;
        var configuration = new ConfigurationBuilder().Build();
        var neo4j = Options.Create(new GraphRagNeo4jOptions
        {
            Uri = "bolt://127.0.0.1:17688",
            Username = "neo4j",
            Password = GraphRagNeo4jCredentialStore.Resolve(configuration),
            Database = "neo4j",
            ConnectionTimeoutSeconds = 5,
        });
        var runtimeOptions = Options.Create(
            new GraphRagNeo4jRuntimeOptions { Mode = "managed" });
        await using var store = new Neo4jGraphStore(
            neo4j,
            runtimeOptions,
            NullLogger<Neo4jGraphStore>.Instance);
        await using var runtime = new Neo4jRuntime(
            runtimeOptions,
            neo4j,
            store,
            new DefaultHttpClientFactory(),
            NullLogger<Neo4jRuntime>.Instance);

        Assert.True(
            await runtime.EnsureAvailableAsync(),
            $"{runtime.Status}: {runtime.LastError}");
        Assert.True(await store.PingAsync());
        await store.EnsureSchemaAsync();
    }

    [FblNeo4jAcceptanceFact]
    [Trait("Category", "ExternalAcceptance")]
    public async Task Neo4jPublish_PreservesActiveOnCancellationAndKeepsOnlyActiveAndPrevious()
    {
        _managedNeo4jUsed = true;
        var configuration = new ConfigurationBuilder().Build();
        var neo4jValue = new GraphRagNeo4jOptions
        {
            Uri = "bolt://127.0.0.1:17688",
            Username = "neo4j",
            Password = GraphRagNeo4jCredentialStore.Resolve(configuration),
            Database = "neo4j",
            ConnectionTimeoutSeconds = 5,
        };
        var neo4j = Options.Create(neo4jValue);
        var runtimeOptions = Options.Create(
            new GraphRagNeo4jRuntimeOptions { Mode = "managed" });
        await using var store = new Neo4jGraphStore(
            neo4j,
            runtimeOptions,
            NullLogger<Neo4jGraphStore>.Instance);
        await using var runtime = new Neo4jRuntime(
            runtimeOptions,
            neo4j,
            store,
            new DefaultHttpClientFactory(),
            NullLogger<Neo4jRuntime>.Instance);
        Assert.True(
            await runtime.EnsureAvailableAsync(),
            $"{runtime.Status}: {runtime.LastError}");

        const string projectId = "neo4j-v3-atomic-acceptance";
        await store.DeleteProjectAsync(projectId);
        try
        {
            var first = SmallSnapshot(projectId, "manifest-v1", "第一版");
            var cancelled = SmallSnapshot(projectId, "manifest-cancelled", "取消版");
            var second = SmallSnapshot(projectId, "manifest-v2", "第二版");
            var third = SmallSnapshot(projectId, "manifest-v3", "第三版");
            await store.PublishAsync(first);

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => store.PublishAsync(cancelled, cancellation.Token));
            }
            Assert.Equal(first.ManifestVersion,
                await store.GetActiveManifestAsync(projectId));

            await store.PublishAsync(second);
            await store.PublishAsync(third);
            Assert.Equal(third.ManifestVersion,
                await store.GetActiveManifestAsync(projectId));

            // 可視化初始圖必須以 relationship 為核心，並保留 UI 使用的語意化展開模式。
            var visual = await store.GetViewerGraphAsync(
                projectId,
                limit: 2,
                filters: null);
            Assert.Equal(2, visual.LoadedNodes);
            Assert.Single(visual.Edges);

            var incoming = await store.GetVisualNeighborsAsync(
                projectId,
                ["code:csharp:acceptance.target"],
                depth: 1,
                limit: 10,
                mode: "in");
            Assert.Equal(2, incoming.LoadedNodes);
            Assert.Single(incoming.Edges);

            var outgoing = await store.GetVisualNeighborsAsync(
                projectId,
                ["code:csharp:acceptance.caller"],
                depth: 1,
                limit: 10,
                mode: "out");
            Assert.Equal(2, outgoing.LoadedNodes);
            Assert.Single(outgoing.Edges);

            // 只 RETURN relationship 時仍必須補齊兩端 node，禁止回傳 orphan edge。
            var relationshipOnly = await store.QueryVisualGraphAsync(
                projectId,
                """
                MATCH (source:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })-[relationship]->(target:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN relationship
                LIMIT $limit
                """,
                limit: 10);
            Assert.Equal(2, relationshipOnly.Graph.LoadedNodes);
            Assert.Single(relationshipOnly.Graph.Edges);

            await using var driver = GraphDatabase.Driver(
                neo4jValue.Uri,
                AuthTokens.Basic(neo4jValue.Username, neo4jValue.Password));
            await using var session = driver.AsyncSession(options =>
                options.WithDatabase(neo4jValue.Database));
            var versionsCursor = await session.RunAsync(
                """
                MATCH (n:GraphEntity {projectId: $projectId})
                RETURN n.graphVersion AS version, count(*) AS count
                ORDER BY version
                """,
                new { projectId });
            var versions = await versionsCursor.ToListAsync(record =>
                record["version"].As<string>());
            Assert.Equal(
                [second.ManifestVersion, third.ManifestVersion],
                versions);

            var pointerCursor = await session.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                RETURN p.activeManifestVersion AS active,
                       p.previousManifestVersion AS previous
                """,
                new { projectId });
            var pointer = await pointerCursor.SingleAsync();
            Assert.Equal(third.ManifestVersion, pointer["active"].As<string>());
            Assert.Equal(second.ManifestVersion, pointer["previous"].As<string>());

            var typesCursor = await session.RunAsync(
                """
                MATCH (:GraphEntity {projectId: $projectId})-[r]->
                      (:GraphEntity {projectId: $projectId})
                RETURN DISTINCT type(r) AS type
                ORDER BY type
                """,
                new { projectId });
            var types = await typesCursor.ToListAsync(record =>
                record["type"].As<string>());
            Assert.All(types, type => Assert.Contains(
                type,
                Enum.GetValues<GraphEdgeKind>().Select(RelationshipTypeForTest)));
        }
        finally
        {
            await store.DeleteProjectAsync(projectId);
            Assert.Null(await store.GetActiveManifestAsync(projectId));
            Assert.Equal((0, 0), await store.GetStatsAsync(projectId));
        }
    }

    [FblNeo4jAcceptanceFact]
    [Trait("Category", "ExternalAcceptance")]
    public async Task RealFblGraph_PublishesToNeo4jAndAnswersFiveGoldenQuestions()
    {
        _managedNeo4jUsed = true;
        var root = Path.GetFullPath(
            Environment.GetEnvironmentVariable("WINGMAN_FBL_ACCEPTANCE_ROOT")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_ACCEPTANCE_ROOT。"));
        var connectionString =
            Environment.GetEnvironmentVariable("WINGMAN_FBL_ACCEPTANCE_SQLSERVER")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_ACCEPTANCE_SQLSERVER。");
        var configuration = new ConfigurationBuilder().Build();
        var runtimeOptions = Options.Create(
            new GraphRagNeo4jRuntimeOptions { Mode = "managed" });
        var neo4j = Options.Create(new GraphRagNeo4jOptions
        {
            Uri = "bolt://127.0.0.1:17688",
            Username = "neo4j",
            Password = GraphRagNeo4jCredentialStore.Resolve(configuration),
            Database = "neo4j",
            ConnectionTimeoutSeconds = 5,
            WriteBatchSize = 2_000,
        });
        await using var store = new Neo4jGraphStore(
            neo4j,
            runtimeOptions,
            NullLogger<Neo4jGraphStore>.Instance);
        await using var runtime = new Neo4jRuntime(
            runtimeOptions,
            neo4j,
            store,
            new DefaultHttpClientFactory(),
            NullLogger<Neo4jRuntime>.Instance);
        Assert.True(
            await runtime.EnsureAvailableAsync(),
            $"{runtime.Status}: {runtime.LastError}");

        const string projectId = "fbl-real-neo4j-acceptance";
        await store.DeleteProjectAsync(projectId);
        try
        {
            var project = new ProjectEntity
            {
                Id = projectId,
                Name = "FBL Neo4j acceptance",
                RootPath = root,
            };
            var projects = new InMemoryProjectRepository(project);
            var manifests = new InMemoryManifestStore();
            var sql = new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance);
            var indexing = new GraphIndexingService(
                [
                    new CSharpGraphExtractor(NullLogger<CSharpGraphExtractor>.Instance),
                    new JavaGraphExtractor(NullLogger<JavaGraphExtractor>.Instance),
                    new FrontendGraphExtractor(NullLogger<FrontendGraphExtractor>.Instance),
                    sql,
                ],
                sql,
                new ProjectGraphDatabaseExtractor(
                    sql,
                    NullLogger<ProjectGraphDatabaseExtractor>.Instance),
                store,
                runtime,
                projects,
                manifests,
                new FixedDatabaseSourceProvider(
                    new GraphDatabaseSource(
                        ProjectDatabaseProvider.SqlServer,
                        connectionString,
                        "FBL_SPV_SIT")),
                Options.Create(new GraphIndexingOptions()),
                NullLogger<GraphIndexingService>.Instance);
            var indexed = await indexing.IndexProjectAsync(projectId);
            var stats = await store.GetStatsAsync(projectId);
            Assert.Equal(indexed.NodeCount, stats.Nodes);
            Assert.Equal(indexed.EdgeCount, stats.Edges);
            Assert.True(stats.Nodes > 1_000);
            Assert.True(stats.Edges > 1_000);

            var schema = await store.GetVisualSchemaAsync(projectId);
            Assert.Equal(4, schema.NodeKinds.Count);
            Assert.All(schema.RelationshipTypes, facet =>
                Assert.Contains(facet.Name,
                    Enum.GetValues<GraphEdgeKind>().Select(ToRelationshipType)));
            var visual = await store.QueryVisualGraphAsync(
                projectId,
                """
                MATCH (n:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN n
                ORDER BY n.id
                LIMIT $limit
                """,
                5);
            Assert.NotEmpty(visual.Graph.Nodes);

            var retrieval = new GraphRetrievalService(
                store,
                Options.Create(new GraphRetrievalOptions
                {
                    SeedLimit = 16,
                    MaximumNodes = 80,
                    MaximumEdges = 120,
                    MaximumDepth = 4,
                    NeighborsPerNode = 50,
                }),
                NullLogger<GraphRetrievalService>.Instance);
            var failures = new List<string>();
            await VerifyGolden(
                "債券交易作廢 bug",
                ["債券", "Bond", "Transaction", "作廢", "Confirm", "覆核"],
                [
                    ("Menu", context => HasRoleNode(context, GraphRoles.MenuFeature, "債券")),
                    ("Route", context => HasNode(context, GraphNodeKind.EntryPoint, "bondtransaction")),
                    ("Controller/BZ", context => HasNode(context, GraphNodeKind.Code, "bondtransaction")),
                    ("Data", context => context.Nodes.Any(item => item.Node.Kind == GraphNodeKind.Data)),
                    ("Confirm", context => context.Nodes.Any(item =>
                        item.Node.Role == GraphRoles.ApprovalFeature)),
                ]);
            await VerifyGolden(
                "新增商品 CSV 格式",
                ["CSV", "Product", "商品", "格式", "Import", "Upload"],
                [
                    ("ProductType", context => context.Nodes.Any(item =>
                        item.Node.Role is GraphRoles.ProductType or GraphRoles.CustomProductType)),
                    ("CsvFormat", context => context.Nodes.Any(item =>
                        item.Node.Role == GraphRoles.CsvFormat)),
                    ("ImportCode", context => HasNode(context, GraphNodeKind.Code, "csv") ||
                                              HasNode(context, GraphNodeKind.Code, "import")),
                ]);
            await VerifyGolden(
                "批次報表寄送內容錯誤",
                ["Batch", "Report", "報表", "Schedule", "Task", "DataSource"],
                [
                    ("Schedule", context => context.Nodes.Any(item =>
                        item.Node.Role == GraphRoles.Schedule)),
                    ("Task", context => context.Nodes.Any(item =>
                        item.Node.Role == GraphRoles.ScheduledTask)),
                    ("BatchReport", context => context.Nodes.Any(item =>
                        item.Node.Role == GraphRoles.BatchReport)),
                    ("Report", context => context.Nodes.Any(item =>
                        item.Node.Role is GraphRoles.CustomReport or GraphRoles.ReportPlugin)),
                    ("DataSource/Data", context => context.Nodes.Any(item =>
                        item.Node.Role is GraphRoles.ReportDataSource or GraphRoles.Table or
                            GraphRoles.View or GraphRoles.Procedure)),
                ]);
            await VerifyGolden(
                "覆核後資料沒有更新",
                ["Confirm", "覆核", "Maintain", "維護", "更新", "Write", "Save"],
                [
                    ("Maintain", context => context.Nodes.Any(item =>
                        item.Node.Role == GraphRoles.MenuFeature)),
                    ("Confirm", context => context.Nodes.Any(item =>
                        item.Node.Role == GraphRoles.ApprovalFeature)),
                    ("ConfirmSourceType", context => context.Edges.Any(edge =>
                        edge.Kind == GraphEdgeKind.Triggers &&
                        edge.Evidence.Any(evidence =>
                            evidence.Details?.ContainsKey("confirmSourceType") == true))),
                    ("WritePath", context => context.Edges.Any(edge =>
                        edge.Kind == GraphEdgeKind.Writes)),
                ]);
            // 這是使用者實際遇到「索引完成卻回答資訊不足」的問題，必須固定成回歸案例。
            // 驗收不要求把所有庫存相關功能塞入 context，只要求能從選單入口一路定位到
            // 前端、Controller 與資料層，讓模型有足夠證據解釋主要資料流。
            await VerifyGolden(
                "關於庫存，給我解釋整個資料流是怎麼運行的？",
                ["庫存", "Inventory", "Position", "Holding", "InventoryReport"],
                [
                    ("Menu", context => HasRoleNode(context, GraphRoles.MenuFeature, "庫存")),
                    ("Route", context => HasNode(
                        context, GraphNodeKind.EntryPoint, "inventoryreport")),
                    ("Frontend", context => context.Nodes.Any(item =>
                        item.Node.Kind == GraphNodeKind.Code &&
                        item.Node.Role is GraphRoles.FrontendPage or GraphRoles.Module &&
                        SearchText(item.Node).Contains(
                            "inventoryreport",
                            StringComparison.OrdinalIgnoreCase))),
                    ("Controller", context => context.Nodes.Any(item =>
                        item.Node.Kind == GraphNodeKind.Code &&
                        item.Node.Role == GraphRoles.Controller &&
                        SearchText(item.Node).Contains(
                            "inventoryreport",
                            StringComparison.OrdinalIgnoreCase))),
                    ("Data", context => context.Nodes.Any(item =>
                        item.Node.Kind == GraphNodeKind.Data)),
                    ("DataPath", context => context.Edges.Any(edge =>
                        edge.Kind is GraphEdgeKind.Reads or GraphEdgeKind.Writes)),
                ],
                minimumCoverage: 1);
            Assert.True(failures.Count == 0,
                string.Join(Environment.NewLine + Environment.NewLine, failures));

            async Task VerifyGolden(
                string question,
                IReadOnlyList<string> relevanceTerms,
                IReadOnlyList<(string Name, Func<GraphRetrievalContext, bool> Match)> expected,
                double minimumCoverage = 0.8)
            {
                var retrievalClock = Stopwatch.StartNew();
                var context = await retrieval.LocalSearchAsync(projectId, question);
                retrievalClock.Stop();
                var matched = expected.Where(item => item.Match(context))
                    .Select(item => item.Name)
                    .ToHashSet(StringComparer.Ordinal);
                var coverage = expected.Count == 0
                    ? 1
                    : (double)matched.Count / expected.Count;
                var relevant = RelevantNodeIds(context, relevanceTerms);
                var noiseRatio = context.Nodes.Count == 0
                    ? 1
                    : 1 - (double)relevant.Count / context.Nodes.Count;
                Console.WriteLine(
                    $"FBL local question={question}, elapsed={retrievalClock.ElapsedMilliseconds}ms, " +
                    $"coverage={coverage:P0}, noise={noiseRatio:P0}");
                if (coverage >= minimumCoverage &&
                    noiseRatio <= 0.75 &&
                    retrievalClock.Elapsed < TimeSpan.FromSeconds(1.5))
                    return;
                var top = string.Join(
                    ", ",
                    context.Nodes.Take(30).Select(item =>
                        $"{item.Node.Role}:{item.Node.Name}"));
                failures.Add(
                    $"[{question}] coverage={coverage:P0}, noise={noiseRatio:P0}, " +
                    $"elapsed={retrievalClock.ElapsedMilliseconds}ms, " +
                    $"matched=[{string.Join(", ", matched)}], nodes={context.Nodes.Count}, " +
                    $"edges={context.Edges.Count}, top=[{top}]");
            }
        }
        finally
        {
            await store.DeleteProjectAsync(projectId);
            Assert.Null(await store.GetActiveManifestAsync(projectId));
            Assert.Equal((0, 0), await store.GetStatsAsync(projectId));
        }

        static bool HasNode(
            GraphRetrievalContext context,
            GraphNodeKind kind,
            string text) =>
            context.Nodes.Any(item =>
                item.Node.Kind == kind &&
                SearchText(item.Node).Contains(text, StringComparison.OrdinalIgnoreCase));

        static bool HasRoleNode(
            GraphRetrievalContext context,
            string role,
            string text) =>
            context.Nodes.Any(item =>
                item.Node.Role == role &&
                SearchText(item.Node).Contains(text, StringComparison.OrdinalIgnoreCase));

        static HashSet<string> RelevantNodeIds(
            GraphRetrievalContext context,
            IReadOnlyList<string> terms)
        {
            var direct = context.Nodes
                .Where(item => terms.Any(term =>
                    SearchText(item.Node).Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Node.Id)
                .ToHashSet(StringComparer.Ordinal);
            var relevant = new HashSet<string>(direct, StringComparer.Ordinal);
            foreach (var edge in context.Edges)
            {
                if (direct.Contains(edge.SourceId)) relevant.Add(edge.TargetId);
                if (direct.Contains(edge.TargetId)) relevant.Add(edge.SourceId);
            }
            return relevant;
        }

        static string SearchText(GraphNode node) =>
            $"{node.Name} {node.SearchableText} {string.Join(' ', node.Aliases)} " +
            $"{node.FilePath} {string.Join(' ', node.Attributes.Values)}";

        static string ToRelationshipType(GraphEdgeKind kind) => kind switch
        {
            GraphEdgeKind.RoutesTo => "ROUTES_TO",
            GraphEdgeKind.Handles => "HANDLES",
            GraphEdgeKind.Calls => "CALLS",
            GraphEdgeKind.DispatchesTo => "DISPATCHES_TO",
            GraphEdgeKind.Triggers => "TRIGGERS",
            GraphEdgeKind.Reads => "READS",
            GraphEdgeKind.Writes => "WRITES",
            GraphEdgeKind.MapsTo => "MAPS_TO",
            GraphEdgeKind.DependsOn => "DEPENDS_ON",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    [FblGraphAcceptanceFact]
    [Trait("Category", "ExternalAcceptance")]
    public async Task RealFblGeneratedDal_ResolvesAsyncConfirmWriteRelationship()
    {
        var root = Path.GetFullPath(
            Environment.GetEnvironmentVariable("WINGMAN_FBL_ACCEPTANCE_ROOT")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_ACCEPTANCE_ROOT。"));
        var definition = Path.Combine(
            root, "RMDBDefinition", "DDAsyncConfirm.cs");
        var dataAccess = Path.Combine(
            root, "RMDAL", "DALAsyncConfirmBase.cs");
        Assert.True(File.Exists(definition), $"缺少真實資料定義檔：{definition}");
        Assert.True(File.Exists(dataAccess), $"缺少真實 DAL 檔：{dataAccess}");

        var fragment = await new SqlServerGraphExtractor(
                NullLogger<SqlServerGraphExtractor>.Instance)
            .ExtractAsync(root, [definition, dataAccess]);
        var snapshot = GraphAssembler.Assemble(
            "fbl-dal-acceptance",
            "manifest",
            DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor(
                "acceptance",
                new Dictionary<string, string>
                {
                    ["sqlserver-scriptdom-v3"] = "3.2.0",
                }),
            "tree",
            "full",
            [],
            [fragment]);

        var owner = Assert.Single(snapshot.Nodes, node =>
            node.Id == "code:csharp:apex.riskmaster.dal.asyncconfirm");
        Assert.Contains("資料寫入", owner.SearchableText);
        Assert.Contains(snapshot.Edges, edge =>
            edge.SourceId == owner.Id &&
            edge.Kind == GraphEdgeKind.Writes &&
            edge.TargetId.EndsWith(
                "/dbo/table/tblasyncconfirm",
                StringComparison.Ordinal));
    }

    [FblGraphAcceptanceFact]
    [Trait("Category", "ExternalAcceptance")]
    public async Task RealFblSourceAndDatabase_ProducesUsefulBoundedGraphAndFastNoOp()
    {
        var root = Path.GetFullPath(
            Environment.GetEnvironmentVariable("WINGMAN_FBL_ACCEPTANCE_ROOT")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_ACCEPTANCE_ROOT。"));
        var connectionString =
            Environment.GetEnvironmentVariable("WINGMAN_FBL_ACCEPTANCE_SQLSERVER")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_ACCEPTANCE_SQLSERVER。");
        Assert.True(Directory.Exists(root), $"FBL source root 不存在：{root}");

        var project = new ProjectEntity
        {
            Id = "fbl-real-acceptance",
            Name = "FBL real acceptance",
            RootPath = root,
        };
        var projects = new InMemoryProjectRepository(project);
        var manifests = new InMemoryManifestStore();
        var store = new InMemoryGraphStore();
        var sql = new SqlServerGraphExtractor(
            NullLogger<SqlServerGraphExtractor>.Instance);
        var service = new GraphIndexingService(
            [
                new CSharpGraphExtractor(NullLogger<CSharpGraphExtractor>.Instance),
                new JavaGraphExtractor(NullLogger<JavaGraphExtractor>.Instance),
                new FrontendGraphExtractor(NullLogger<FrontendGraphExtractor>.Instance),
                sql,
            ],
            sql,
            new ProjectGraphDatabaseExtractor(
                sql,
                NullLogger<ProjectGraphDatabaseExtractor>.Instance),
            store,
            new AvailableNeo4jRuntime(),
            projects,
            manifests,
            new FixedDatabaseSourceProvider(
                new GraphDatabaseSource(
                    ProjectDatabaseProvider.SqlServer,
                    connectionString,
                    "FBL_SPV_SIT")),
            Options.Create(new GraphIndexingOptions()),
            NullLogger<GraphIndexingService>.Instance);

        await service.IndexProjectAsync(project.Id);
        var snapshot = Assert.IsType<GraphSnapshot>(store.LastSnapshot);
        var fullRun = Assert.IsType<GraphIndexRun>(service.GetLastRun(project.Id));
        Console.WriteLine(
            $"FBL full elapsed={fullRun.ElapsedMilliseconds}ms, " +
            $"stages={JsonSerializer.Serialize(fullRun.StageDurationsMilliseconds)}, " +
            $"nodes={fullRun.NodeCount}, edges={fullRun.EdgeCount}");
        Assert.True(
            fullRun.ElapsedMilliseconds < TimeSpan.FromSeconds(60).TotalMilliseconds,
            $"FBL warm full 耗時 {fullRun.ElapsedMilliseconds}ms，超過 60 秒 gate；" +
            $"stages={JsonSerializer.Serialize(fullRun.StageDurationsMilliseconds)}");
        GraphAssembler.ValidateSnapshot(snapshot);
        var firstSnapshot = snapshot;

        var requirements = new List<string>();
        Require(snapshot.Nodes.Select(node => node.Kind).Distinct().Count() == 4,
            "未產生完整四種 NodeKind");
        Require(snapshot.Nodes.Count < 100_000,
            $"節點數 {snapshot.Nodes.Count} 超過 bounded graph 上限");
        Require(snapshot.Edges.Count < 500_000,
            $"關係數 {snapshot.Edges.Count} 超過 bounded graph 上限");
        Require(snapshot.Nodes.Any(node => node.Role == GraphRoles.MenuFeature),
            "缺少 Menu Feature");
        Require(snapshot.Nodes.Any(node => node.Role == GraphRoles.Controller),
            "缺少 Controller Code");
        Require(snapshot.Nodes.Any(node => node.Role == GraphRoles.FrontendPage),
            "缺少 frontend-page EntryPoint");
        Require(snapshot.Nodes.Any(node => node.Role == GraphRoles.Table),
            "缺少 SQL Table");
        foreach (var kind in new[]
                 {
                     GraphEdgeKind.RoutesTo,
                     GraphEdgeKind.Handles,
                     GraphEdgeKind.Calls,
                     GraphEdgeKind.MapsTo,
                     GraphEdgeKind.Triggers,
                 })
            Require(snapshot.Edges.Any(edge => edge.Kind == kind), $"缺少 {kind} 關係");
        Require(snapshot.Edges.Any(edge =>
                edge.Kind is GraphEdgeKind.Reads or GraphEdgeKind.Writes),
            "缺少 READS/WRITES 資料路徑");

        Require(snapshot.Edges.Any(edge =>
            edge.Kind == GraphEdgeKind.RoutesTo &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.SourceId &&
                node.Role == GraphRoles.MenuFeature) &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.TargetId &&
                node.Kind == GraphNodeKind.EntryPoint)),
            "缺少 Menu→HTTP EntryPoint");
        Require(snapshot.Edges.Any(edge =>
            edge.Kind == GraphEdgeKind.Triggers &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.SourceId &&
                node.Role == GraphRoles.MenuFeature) &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.TargetId &&
                node.Role == GraphRoles.ApprovalFeature)),
            "缺少 Maintain Menu→Confirm Feature");
        Require(snapshot.Edges.Any(edge =>
            edge.Kind == GraphEdgeKind.MapsTo &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.SourceId &&
                node.Role is GraphRoles.ProductType or GraphRoles.CustomProductType) &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.TargetId &&
                node.Role == GraphRoles.CsvFormat)),
            "缺少 ProductType/CustomType→CSV Format");
        Require(snapshot.Edges.Any(edge =>
            edge.Kind == GraphEdgeKind.Triggers &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.SourceId &&
                node.Role == GraphRoles.Schedule) &&
            snapshot.Nodes.Any(node =>
                node.Id == edge.TargetId &&
                node.Role == GraphRoles.ScheduledTask)),
            "缺少 Schedule→Task");

        var serialized = JsonSerializer.Serialize(snapshot);
        Require(!serialized.Contains("password=", StringComparison.OrdinalIgnoreCase),
            "snapshot 洩漏 password");
        Require(!serialized.Contains("user id=", StringComparison.OrdinalIgnoreCase),
            "snapshot 洩漏 user id");
        Require(!serialized.Contains("@assword", StringComparison.OrdinalIgnoreCase),
            "snapshot 洩漏 DB secret");
        Require(!string.Concat(snapshot.Nodes.Select(node => node.SearchableText)).Contains('@'),
            "snapshot searchable text 含 Email/contact");
        Assert.True(requirements.Count == 0,
            $"FBL graph nodes={snapshot.Nodes.Count}, edges={snapshot.Edges.Count}, " +
            $"diagnostics={snapshot.Diagnostics.Count}, gaps={snapshot.CapabilityGaps.Count}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, requirements.Select(value => "- " + value)));

        var noOpClock = Stopwatch.StartNew();
        await service.IndexProjectAsync(project.Id);
        noOpClock.Stop();
        var secondMode = service.GetLastRun(project.Id)?.Mode;
        Console.WriteLine(
            $"FBL no-op elapsed={noOpClock.ElapsedMilliseconds}ms, mode={secondMode}");
        var secondSnapshot = store.LastSnapshot!;
        var changedArtifacts = firstSnapshot.Artifacts
            .Join(
                secondSnapshot.Artifacts,
                first => first.Id,
                second => second.Id,
                (first, second) => (first, second),
                StringComparer.Ordinal)
            .Where(pair => !string.Equals(
                pair.first.ContentHash,
                pair.second.ContentHash,
                StringComparison.Ordinal))
            .Select(pair => pair.first.Path)
            .Take(20)
            .ToList();
        var removedArtifacts = firstSnapshot.Artifacts.Select(item => item.Id)
            .Except(secondSnapshot.Artifacts.Select(item => item.Id), StringComparer.Ordinal)
            .Take(20);
        var addedArtifacts = secondSnapshot.Artifacts.Select(item => item.Id)
            .Except(firstSnapshot.Artifacts.Select(item => item.Id), StringComparer.Ordinal)
            .Take(20);
        Assert.True(string.Equals(secondMode, "no-op", StringComparison.Ordinal),
            $"預期 no-op，實際 {secondMode}；firstFingerprint={firstSnapshot.WorkingTreeFingerprint}, " +
            $"secondFingerprint={secondSnapshot.WorkingTreeFingerprint}, " +
            $"changed=[{string.Join(", ", changedArtifacts)}], " +
            $"removed=[{string.Join(", ", removedArtifacts)}], " +
            $"added=[{string.Join(", ", addedArtifacts)}]");
        Assert.True(noOpClock.Elapsed < TimeSpan.FromSeconds(2),
            $"FBL no-op 耗時 {noOpClock.Elapsed.TotalMilliseconds:F0}ms，超過 2 秒 gate。");

        void Require(bool condition, string message)
        {
            if (!condition) requirements.Add(message);
        }
    }

    private Fixture CreateFixture(IGraphDatabaseSourceProvider? databaseSources = null)
    {
        var project = new ProjectEntity
        {
            Id = "project",
            Name = "Fixture",
            RootPath = _root,
        };
        var projects = new InMemoryProjectRepository(project);
        var manifests = new InMemoryManifestStore();
        var store = new InMemoryGraphStore();
        var extractor = new FixtureExtractor();
        var sql = new SqlServerGraphExtractor(
            NullLogger<SqlServerGraphExtractor>.Instance);
        var service = new GraphIndexingService(
            [extractor],
            sql,
            new ProjectGraphDatabaseExtractor(
                sql,
                NullLogger<ProjectGraphDatabaseExtractor>.Instance),
            store,
            new AvailableNeo4jRuntime(),
            projects,
            manifests,
            databaseSources ?? new NullDatabaseSourceProvider(),
            Options.Create(new GraphIndexingOptions()),
            NullLogger<GraphIndexingService>.Instance);
        return new Fixture(service, projects, manifests, store, extractor);
    }

    private CSharpFixture CreateCSharpFixture(
        IGraphDatabaseSourceProvider? databaseSources = null)
    {
        var project = new ProjectEntity
        {
            Id = "project",
            Name = "CSharp Fixture",
            RootPath = _root,
        };
        var projects = new InMemoryProjectRepository(project);
        var manifests = new InMemoryManifestStore();
        var store = new InMemoryGraphStore();
        var csharp = new CSharpGraphExtractor(
            NullLogger<CSharpGraphExtractor>.Instance);
        var sql = new SqlServerGraphExtractor(
            NullLogger<SqlServerGraphExtractor>.Instance);
        var service = new GraphIndexingService(
            [csharp],
            sql,
            new ProjectGraphDatabaseExtractor(
                sql,
                NullLogger<ProjectGraphDatabaseExtractor>.Instance),
            store,
            new AvailableNeo4jRuntime(),
            projects,
            manifests,
            databaseSources ?? new NullDatabaseSourceProvider(),
            Options.Create(new GraphIndexingOptions()),
            NullLogger<GraphIndexingService>.Instance);
        return new CSharpFixture(service, store);
    }

    /// <summary>
    /// 使用桌面程式目前已發布的投資系統 Graph，驗證十五題回答 context 與一百次 warm retrieval。
    /// 此測試不重建索引、不呼叫外部 SQL，也不修改使用者專案；只讀取 Neo4j 與 source evidence。
    /// </summary>
    [FblNeo4jAcceptanceFact]
    [Trait("Category", "ExternalAcceptance")]
    public async Task LiveFblGraph_AnswersGoldenQuestionsAndMeetsWarmP95()
    {
        var projectId = Environment.GetEnvironmentVariable("WINGMAN_FBL_LIVE_PROJECT_ID")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_LIVE_PROJECT_ID。");
        var root = Path.GetFullPath(
            Environment.GetEnvironmentVariable("WINGMAN_FBL_ACCEPTANCE_ROOT")
            ?? throw new InvalidOperationException("缺少 WINGMAN_FBL_ACCEPTANCE_ROOT。"));
        var configuration = new ConfigurationBuilder().Build();
        var neo4j = Options.Create(new GraphRagNeo4jOptions
        {
            Uri = "bolt://127.0.0.1:17688",
            Username = "neo4j",
            Password = GraphRagNeo4jCredentialStore.Resolve(configuration),
            Database = "neo4j",
            ConnectionTimeoutSeconds = 5,
        });
        await using var store = new Neo4jGraphStore(
            neo4j,
            Options.Create(new GraphRagNeo4jRuntimeOptions { Mode = "managed" }),
            NullLogger<Neo4jGraphStore>.Instance);
        Assert.NotNull(await store.GetActiveManifestAsync(projectId));
        var retrieval = new GraphRetrievalService(
            store,
            Options.Create(new GraphRetrievalOptions()),
            NullLogger<GraphRetrievalService>.Instance);
        (string Question, string[] ExpectedTerms, string[] ExpectedFiles,
            GraphNodeKind[] RequiredKinds)[] golden =
        [
            ("債券交易作廢 bug", ["債券", "Bond", "Confirm"],
                ["bondtransactioncontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("新增商品 CSV 格式", ["CSV", "Product", "商品"],
                ["addproductcontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("批次報表寄送內容錯誤", ["Batch", "Report", "報表"],
                ["batchreportcontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("覆核後資料沒有更新", ["Confirm", "覆核", "維護"],
                ["announcementcontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("關於庫存，給我解釋整個資料流是怎麼運行的？", ["庫存", "Inventory", "Position"],
                ["inventoryreportcontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.EntryPoint, GraphNodeKind.Code]),
            ("會計公報分類資料維護如何讀取 tblRawData？請列出 Controller、主要方法、SQL 與行號。",
                ["AccountingPurposeCSV", "tblRawData"],
                ["accountingpurposecsvcontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code, GraphNodeKind.Data]),
            ("債券交易有哪些功能？", ["債券", "Bond"],
                ["bondtransactioncontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("股票交易在哪裡實作？", ["股票", "Stock", "Equity"],
                ["equitytransactioncontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("基金交易存檔流程怎麼走？", ["基金", "Fund"],
                ["alternativefundtransactioncontroller.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("交割日被哪些程式與資料表使用？", ["SettlementDate", "SettleDate", "ValueDate"],
                ["transactionmanagerbondcontroller.cs"], [GraphNodeKind.Code, GraphNodeKind.Data]),
            ("日結批次如何執行？", ["DailyClosing", "EOD", "日結"],
                ["batchdayend.cs"], [GraphNodeKind.Feature, GraphNodeKind.Code]),
            ("損益報表使用哪些資料？", ["ProfitLoss", "PnL", "損益"],
                ["positionprofit_reportkernel.cs"], [GraphNodeKind.Code, GraphNodeKind.Data]),
            ("交易存檔後為什麼沒有更新部位？", ["Position", "Holding", "Inventory", "部位"],
                ["positionprocesscontroller.cs", "inventoryreportcontroller.cs"], [GraphNodeKind.Code]),
            ("修改交割日會影響哪些地方？", ["SettlementDate", "SettleDate", "ValueDate"],
                ["transactionmanagerbondcontroller.cs"], [GraphNodeKind.Code, GraphNodeKind.Data]),
            ("整個系統有哪些批次與報表流程？", ["Batch", "Report", "Schedule", "報表"],
                [], [GraphNodeKind.Feature]),
            ("登入流程是什麼？", ["AccountController", "ProcessLogin", "LoginAndPassword"],
                ["loginandpassword.cs"],
                [GraphNodeKind.Code]),
        ];

        var matched = 0;
        var relevantFileMatched = 0;
        var coldDurations = new List<double>();
        foreach (var item in golden)
        {
            var context = await retrieval.LocalSearchAsync(projectId, item.Question);
            var searchable = string.Join(' ', context.Nodes.Select(node =>
                $"{node.Node.Name} {node.Node.SearchableText} {node.Node.FilePath} " +
                $"{string.Join(' ', node.Node.Aliases)}"));
            if (item.Question.StartsWith("登入流程", StringComparison.Ordinal))
            {
                Assert.Contains(
                    context.Nodes,
                    value => string.Equals(
                        value.Node.FilePath,
                        "loginandpassword/loginandpassword.cs",
                        StringComparison.OrdinalIgnoreCase));
            }
            if (context.Nodes.Count > 0 && item.ExpectedTerms.Any(term =>
                    searchable.Contains(term, StringComparison.OrdinalIgnoreCase)))
                matched++;
            Assert.All(item.RequiredKinds, kind => Assert.Contains(
                context.Nodes, value => value.Node.Kind == kind));
            var coldClock = Stopwatch.StartNew();
            var prompt = await retrieval.BuildAnswerPromptAsync(
                projectId, root, item.Question);
            coldClock.Stop();
            coldDurations.Add(coldClock.Elapsed.TotalMilliseconds);
            if (item.Question.StartsWith("登入流程", StringComparison.Ordinal))
            {
                Assert.Contains(
                    "## loginandpassword/loginandpassword.cs",
                    prompt,
                    StringComparison.OrdinalIgnoreCase);
            }
            Console.WriteLine(string.Join(" | ", prompt.Split('\n')
                .Where(line => line.Contains("## ", StringComparison.Ordinal) &&
                               line.Contains(':', StringComparison.Ordinal))
                .Take(12)));
            Assert.True(
                prompt.Contains("# GraphRAG context", StringComparison.Ordinal) ||
                prompt.Contains("# GraphRAG 社群摘要", StringComparison.Ordinal),
                $"[{item.Question}] 沒有 Graph 或 Community context。");
            if (item.ExpectedFiles.Length == 0 ||
                item.ExpectedFiles.Any(file =>
                    prompt.Contains(file, StringComparison.OrdinalIgnoreCase)))
                relevantFileMatched++;
            Assert.True(prompt.Length <= 25_000,
                $"[{item.Question}] prompt 長度 {prompt.Length} 超過安全預算。");
            Console.WriteLine(
                $"FBL live question={item.Question}, nodes={context.Nodes.Count}, " +
                $"edges={context.Edges.Count}, prompt={prompt.Length}");
        }
        var minimumRecallCount = (int)Math.Ceiling(golden.Length * 0.8);
        Assert.True(matched >= minimumRecallCount,
            $"{golden.Length} 題只有 {matched} 題命中預期領域詞，低於 80% Recall。");
        Assert.True(relevantFileMatched >= minimumRecallCount,
            $"{golden.Length} 題只有 {relevantFileMatched} 題引用 Golden 相關檔案，低於 80% Recall。");

        // 先暖機，再以十五題輪詢至至少一百次。此計時涵蓋 Graph、Source Reader、
        // Roslyn member 展開、fallback 與 Context Compiler，但不包含模型生成。
        foreach (var item in golden)
            await retrieval.BuildAnswerPromptAsync(projectId, root, item.Question);
        var durations = new List<double>();
        for (var index = 0; index < 105; index++)
        {
            var clock = Stopwatch.StartNew();
            await retrieval.BuildAnswerPromptAsync(
                projectId, root, golden[index % golden.Length].Question);
            clock.Stop();
            durations.Add(clock.Elapsed.TotalMilliseconds);
        }
        durations.Sort();
        var p95 = durations[(int)Math.Ceiling(durations.Count * 0.95) - 1];
        Console.WriteLine(
            $"FBL cold retrieval max={coldDurations.Max():F1}ms, " +
            $"warm full-context P95={p95:F1}ms, samples={durations.Count}");
        Assert.True(p95 < 2_000, $"Warm retrieval P95 {p95:F1}ms 超過 2 秒。");
    }

    /// <summary>
    /// 建立不含 Menu／功能 metadata 的最小 SQLite schema，
    /// 供資料庫指紋與 Feature 健康診斷測試使用。
    /// </summary>
    /// <param name="databasePath">測試資料庫的完整路徑。</param>
    private static async Task CreateSqliteSchemaAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE orders(id INTEGER PRIMARY KEY, settlement_date TEXT);";
        await command.ExecuteNonQueryAsync();
    }

    private CSharpFixture CreateCSharpAndSqlFixture()
    {
        var project = new ProjectEntity
        {
            Id = "project",
            Name = "CSharp SQL Fixture",
            RootPath = _root,
        };
        var projects = new InMemoryProjectRepository(project);
        var manifests = new InMemoryManifestStore();
        var store = new InMemoryGraphStore();
        var csharp = new CSharpGraphExtractor(
            NullLogger<CSharpGraphExtractor>.Instance);
        var sql = new SqlServerGraphExtractor(
            NullLogger<SqlServerGraphExtractor>.Instance);
        var service = new GraphIndexingService(
            [csharp, sql],
            sql,
            new ProjectGraphDatabaseExtractor(
                sql,
                NullLogger<ProjectGraphDatabaseExtractor>.Instance),
            store,
            new AvailableNeo4jRuntime(),
            projects,
            manifests,
            new NullDatabaseSourceProvider(),
            Options.Create(new GraphIndexingOptions()),
            NullLogger<GraphIndexingService>.Instance);
        return new CSharpFixture(service, store);
    }

    private static GraphSnapshot SmallSnapshot(
        string projectId,
        string manifestVersion,
        string marker)
    {
        var caller = new GraphNode(
            "code:csharp:acceptance.caller",
            GraphNodeKind.Code,
            GraphRoles.BusinessService,
            "AcceptanceCaller",
            $"AcceptanceCaller {marker}",
            "csharp",
            "managed-acceptance",
            "active",
            [marker],
            "src/AcceptanceCaller.cs",
            1,
            1,
            new Dictionary<string, string>(),
            [new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                "src/AcceptanceCaller.cs",
                $"由 managed Neo4j 驗收建立{marker}呼叫端。")]);
        var target = new GraphNode(
            "code:csharp:acceptance.target",
            GraphNodeKind.Code,
            GraphRoles.Repository,
            "AcceptanceTarget",
            $"AcceptanceTarget {marker}",
            "csharp",
            "managed-acceptance",
            "active",
            [marker],
            "src/AcceptanceTarget.cs",
            1,
            1,
            new Dictionary<string, string>(),
            [new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                "src/AcceptanceTarget.cs",
                $"由 managed Neo4j 驗收建立{marker}目標端。")]);
        var fragment = new GraphFragment();
        fragment.Nodes.AddRange([caller, target]);
        fragment.Edges.Add(new GraphEdge(
            GraphIdentity.Edge(caller.Id, GraphEdgeKind.Calls, target.Id),
            caller.Id,
            GraphEdgeKind.Calls,
            target.Id,
            [new GraphEvidence(
                GraphEvidenceSource.Ast,
                GraphConfidence.Exact,
                "src/AcceptanceCaller.cs",
                $"由 managed Neo4j 驗收建立{marker} CALLS。")]));
        return GraphAssembler.Assemble(
            projectId,
            manifestVersion,
            DateTimeOffset.UnixEpoch,
            new GraphIndexerDescriptor(
                "acceptance",
                new Dictionary<string, string> { ["fixture"] = "1.0.0" }),
            $"tree-{marker}",
            "full",
            [],
            [fragment]);
    }

    private static string RelationshipTypeForTest(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.RoutesTo => "ROUTES_TO",
        GraphEdgeKind.Handles => "HANDLES",
        GraphEdgeKind.Calls => "CALLS",
        GraphEdgeKind.DispatchesTo => "DISPATCHES_TO",
        GraphEdgeKind.Triggers => "TRIGGERS",
        GraphEdgeKind.Reads => "READS",
        GraphEdgeKind.Writes => "WRITES",
        GraphEdgeKind.MapsTo => "MAPS_TO",
        GraphEdgeKind.DependsOn => "DEPENDS_ON",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public void Dispose()
    {
        if (_managedNeo4jUsed)
            CleanupManagedNeo4jDatabaseFiles();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void CleanupManagedNeo4jDatabaseFiles()
    {
        // Managed acceptance 會寫入大量 transaction log；只執行 DETACH DELETE
        // 不會歸還 C 槽空間。因此 runtime 停止後必須實體移除測試 database，
        // 且任何清理失敗都讓 acceptance 失敗，禁止 best-effort 吞掉錯誤。
        if (GetManagedNeo4jListener())
            throw new InvalidOperationException(
                "Neo4j managed acceptance 結束後仍占用 17688，拒絕在執行中刪除 database。");

        var home = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wingman",
            "neo4j",
            "neo4j-community-5.26.0"));
        var databaseRoot = Path.GetFullPath(Path.Combine(home, "data", "databases"));
        var transactionRoot = Path.GetFullPath(Path.Combine(home, "data", "transactions"));
        DeleteExactChild(databaseRoot, "neo4j");
        DeleteExactChild(transactionRoot, "neo4j");

        static void DeleteExactChild(string root, string child)
        {
            var target = Path.GetFullPath(Path.Combine(root, child));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Neo4j cleanup target 超出核准資料目錄。");
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            if (Directory.Exists(target))
                throw new IOException($"Neo4j 測試資料清理失敗：{target}");
        }

        static bool GetManagedNeo4jListener()
        {
            try
            {
                return System.Net.NetworkInformation.IPGlobalProperties
                    .GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == 17688);
            }
            catch
            {
                // 無法證明 port 已停止時採 fail-closed，避免破壞執行中的 database。
                return true;
            }
        }
    }

    private sealed record Fixture(
        GraphIndexingService Service,
        InMemoryProjectRepository Projects,
        InMemoryManifestStore Manifests,
        InMemoryGraphStore Store,
        FixtureExtractor Extractor);

    private sealed record CSharpFixture(
        GraphIndexingService Service,
        InMemoryGraphStore Store);

    private sealed class FixtureExtractor : IGraphExtractor
    {
        public string Id => "fixture-v3";
        public string Version { get; set; } = "1.0.0";
        public IReadOnlyList<string> LastFiles { get; private set; } = [];

        public Task<GraphFragment> ExtractAsync(
            string projectRoot,
            IReadOnlyList<string> files,
            CancellationToken cancellationToken = default)
        {
            LastFiles = files.ToList();
            var fragment = new GraphFragment();
            fragment.Nodes.Add(new GraphNode(
                "feature:menu:fixture",
                GraphNodeKind.Feature,
                GraphRoles.MenuFeature,
                "測試功能",
                "測試功能",
                "business",
                "fixture",
                "active",
                ["fixture"],
                null,
                null,
                null,
                new Dictionary<string, string>
                {
                    ["menuPath"] = "測試系統 > 測試功能",
                },
                [new GraphEvidence(
                    GraphEvidenceSource.Ast,
                    AgentService.Modules.GraphRAG.GraphConfidence.Exact,
                    GraphIdentity.NormalizePath(
                        Path.GetRelativePath(projectRoot, files.Single())),
                    "由索引測試 fixture 建立業務功能。")]));
            return Task.FromResult(fragment);
        }
    }

    private sealed class NullDatabaseSourceProvider : IGraphDatabaseSourceProvider
    {
        public Task<GraphDatabaseSource?> GetAsync(
            ProjectEntity project,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GraphDatabaseSource?>(null);
    }

    private sealed class FixedDatabaseSourceProvider(GraphDatabaseSource source)
        : IGraphDatabaseSourceProvider
    {
        public Task<GraphDatabaseSource?> GetAsync(
            ProjectEntity project,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<GraphDatabaseSource?>(source);
    }

    /// <summary>允許測試在首次成功索引後切換成失敗資料庫來源。</summary>
    private sealed class MutableDatabaseSourceProvider : IGraphDatabaseSourceProvider
    {
        public GraphDatabaseSource? Source { get; set; }

        public Task<GraphDatabaseSource?> GetAsync(
            ProjectEntity project,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Source);
    }

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class AvailableNeo4jRuntime : INeo4jRuntime
    {
        public string Status => "running";
        public string? LastError => null;

        public Task<bool> EnsureAvailableAsync(
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class InMemoryProjectRepository(ProjectEntity project) : IProjectRepository
    {
        private ProjectEntity _project = project;

        public Task<IReadOnlyList<ProjectEntity>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProjectEntity>>([_project]);

        public Task<ProjectEntity?> GetAsync(string projectId, CancellationToken ct = default) =>
            Task.FromResult<ProjectEntity?>(_project.Id == projectId ? _project : null);

        public Task SaveAsync(ProjectEntity value, CancellationToken ct = default)
        {
            _project = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string projectId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryManifestStore : IProjectIndexManifestStore
    {
        private readonly Dictionary<string, ProjectIndexManifest> _versions =
            new(StringComparer.Ordinal);

        public ProjectIndexManifest? Current { get; private set; }
        public ProjectIndexManifest? Latest { get; private set; }
        public bool FailNextPromote { get; set; }

        public Task SaveAttemptAsync(
            ProjectIndexManifest manifest,
            CancellationToken ct = default)
        {
            _versions[manifest.Version] = manifest;
            Latest = manifest;
            return Task.CompletedTask;
        }

        public Task PromoteAsync(
            ProjectIndexManifest manifest,
            CancellationToken ct = default)
        {
            if (FailNextPromote)
            {
                FailNextPromote = false;
                throw new InvalidOperationException(
                    "simulated manifest promote failure");
            }
            _versions[manifest.Version] = manifest;
            Current = manifest;
            Latest = manifest;
            return Task.CompletedTask;
        }

        public Task<ProjectIndexManifest?> GetCurrentAsync(
            string projectId,
            CancellationToken ct = default) =>
            Task.FromResult(Current?.ProjectId == projectId ? Current : null);

        public Task<ProjectIndexManifest?> GetLatestAttemptAsync(
            string projectId,
            CancellationToken ct = default) =>
            Task.FromResult(Latest?.ProjectId == projectId ? Latest : null);

        public Task<ProjectIndexManifest?> GetByVersionAsync(
            string projectId,
            string version,
            CancellationToken ct = default) =>
            Task.FromResult(
                _versions.TryGetValue(version, out var value) &&
                value.ProjectId == projectId
                    ? value
                    : null);

        public Task DeleteProjectAsync(
            string projectId,
            CancellationToken ct = default)
        {
            Current = null;
            Latest = null;
            _versions.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryGraphStore : IGraphStore
    {
        private IReadOnlyList<GraphCommunityReport> _communityReports = [];

        public int PublishCount { get; private set; }
        public bool FailPublish { get; set; }
        public bool CancelPublish { get; set; }
        public string? ActiveManifest { get; private set; }
        public GraphSnapshot? LastSnapshot { get; private set; }
        public IReadOnlyList<GraphCommunityReport> CommunityReports => _communityReports;

        public void ReplaceCommunityReports(IReadOnlyList<GraphCommunityReport> reports) =>
            _communityReports = reports.ToList();

        public Task PublishAsync(
            GraphSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (FailPublish) throw new InvalidOperationException("simulated publish failure");
            if (CancelPublish) throw new OperationCanceledException("simulated cancellation");
            GraphAssembler.ValidateSnapshot(snapshot);
            PublishCount++;
            ActiveManifest = snapshot.ManifestVersion;
            LastSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<string?> GetActiveManifestAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ActiveManifest);

        public Task<bool> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteProjectAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<GraphSearchHitV3>> SearchAsync(
            string projectId,
            string query,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphSearchHitV3>>([]);
        public Task<IReadOnlyList<GraphNeighborV3>> GetNeighborsAsync(
            string projectId,
            string nodeId,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphNeighborV3>>([]);
        public Task<(int Nodes, int Edges)> GetStatsAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((0, 0));
        public Task<IReadOnlyList<GraphSearchHitV3>> GetCentralNodesAsync(
            string projectId,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GraphSearchHitV3>>([]);
        public Task SaveCommunityReportsAsync(
            string projectId,
            string manifestVersion,
            IReadOnlyList<GraphCommunityReport> reports,
            CancellationToken cancellationToken = default)
        {
            _communityReports = reports.ToList();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<GraphCommunityReport>> ListCommunityReportsAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_communityReports);
        public Task<GraphVisualDataV3> GetViewerGraphAsync(
            string projectId,
            int limit,
            IReadOnlyList<GraphViewerSearchFilter>? filters,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualDataV3([], [], 0, 0, 0, false));
        public Task<GraphVisualSchemaV3> GetVisualSchemaAsync(
            string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualSchemaV3(0, 0, [], [], []));
        public Task<GraphVisualDataV3> GetVisualNeighborsAsync(
            string projectId,
            IReadOnlyList<string> nodeIds,
            int depth,
            int limit,
            string mode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualDataV3([], [], 0, 0, 0, false));
        public Task<GraphVisualQueryResultV3> QueryVisualGraphAsync(
            string projectId,
            string cypher,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GraphVisualQueryResultV3(
                [], [], new GraphVisualDataV3([], [], 0, 0, 0, false)));
    }
}

internal sealed class FblGraphAcceptanceFactAttribute : FactAttribute
{
    public FblGraphAcceptanceFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WINGMAN_RUN_FBL_ACCEPTANCE"),
                "1",
                StringComparison.Ordinal))
            Skip = "設定 WINGMAN_RUN_FBL_ACCEPTANCE=1 才執行真實 FBL source＋DB 驗收。";
    }
}

internal sealed class FblNeo4jAcceptanceFactAttribute : FactAttribute
{
    public FblNeo4jAcceptanceFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("WINGMAN_RUN_FBL_NEO4J_ACCEPTANCE"),
                "1",
                StringComparison.Ordinal))
            Skip = "設定 WINGMAN_RUN_FBL_NEO4J_ACCEPTANCE=1 才執行 managed Neo4j 驗收。";
    }
}
