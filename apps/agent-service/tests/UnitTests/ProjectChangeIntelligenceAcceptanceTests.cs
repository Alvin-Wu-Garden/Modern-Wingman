using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;
using AgentService.Infrastructure.CodeAnalysis;
using AgentService.Infrastructure.CodeGraph;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentService.UnitTests;

public sealed class ProjectIndexAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wingman-index-acceptance-" + Guid.NewGuid().ToString("N"));

    public ProjectIndexAcceptanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CatchUp_HashesChangedInputsAndMarksBuildAndSourceFilesPending()
    {
        var source = Path.Combine(_root, "OrderService.cs");
        var projectFile = Path.Combine(_root, "Orders.csproj");
        await File.WriteAllTextAsync(source, "class OrderService { }");
        await File.WriteAllTextAsync(projectFile, "<Project />");

        var project = Project(_root, "v1");
        var repository = new MemoryProjectRepository(project);
        var current = Manifest("v1", _root,
        [
            FileManifest(_root, source, "class OldOrderService { }"),
            FileManifest(_root, projectFile, "<Project Sdk=\"old\" />"),
        ]);
        var manifests = new MemoryManifestStore(current, current);
        var graph = new MemoryGraphStore("v1");
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle);

        var changed = await service.CatchUpAsync(project.Id);
        var diagnostics = await service.GetDiagnosticsAsync(project.Id);

        Assert.True(changed);
        Assert.Equal(ProjectIndexStatus.PendingChanges, project.IndexStatus);
        Assert.Equal(2, project.PendingFileCount);
        Assert.Equal(
            ["OrderService.cs", "Orders.csproj"],
            diagnostics.PendingFiles.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task CatchUp_DetectsSameLengthContentWithRestoredMtime()
    {
        var source = Path.Combine(_root, "OrderService.cs");
        const string oldContent = "class Before { }";
        const string newContent = "class After_ { }";
        Assert.Equal(Encoding.UTF8.GetByteCount(oldContent), Encoding.UTF8.GetByteCount(newContent));
        await File.WriteAllTextAsync(source, oldContent);
        var stableMtime = File.GetLastWriteTimeUtc(source);
        var current = Manifest("v1", _root,
        [
            new IndexedFileManifest(
                "OrderService.cs",
                "csharp",
                Encoding.UTF8.GetByteCount(oldContent),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldContent))).ToLowerInvariant(),
                LastWriteAt: stableMtime),
        ]);
        await File.WriteAllTextAsync(source, newContent);
        File.SetLastWriteTimeUtc(source, stableMtime);

        var project = Project(_root, "v1");
        var repository = new MemoryProjectRepository(project);
        var manifests = new MemoryManifestStore(current, current);
        var graph = new MemoryGraphStore("v1");
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle);

        Assert.True(await service.CatchUpAsync(project.Id));
        var diagnostics = await service.GetDiagnosticsAsync(project.Id);
        Assert.Equal(["OrderService.cs"], diagnostics.PendingFiles);
    }

    [Fact]
    public async Task ManifestRecovery_PromotesCommittedNeo4jVersionAfterSqlitePointerInterruption()
    {
        var project = Project(_root, "v1");
        var repository = new MemoryProjectRepository(project);
        var v1 = Manifest("v1", _root, [], nodes: 1, edges: 0);
        var interruptedV2 = Manifest("v2", _root, [], status: IndexManifestStatus.Indexing);
        var manifests = new MemoryManifestStore(v1, interruptedV2);
        var graph = new MemoryGraphStore("v2", nodes: 12, edges: 18);
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle);

        var diagnostics = await service.GetDiagnosticsAsync(project.Id);

        Assert.Equal("v2", diagnostics.Current?.Version);
        Assert.Equal(IndexManifestStatus.Fresh, diagnostics.Current?.Status);
        Assert.Equal(12, diagnostics.Current?.NodeCount);
        Assert.Equal(18, diagnostics.Current?.EdgeCount);
        Assert.Equal("v2", project.IndexManifestVersion);
        Assert.Equal(ProjectIndexStatus.Indexed, project.IndexStatus);
    }

    [Fact]
    public async Task ManifestRecovery_UsesCommittedVersionEvenWhenNewerAttemptFailed()
    {
        var project = Project(_root, "v1");
        var repository = new MemoryProjectRepository(project);
        var v1 = Manifest("v1", _root, [], nodes: 1);
        var interruptedV2 = Manifest("v2", _root, [], status: IndexManifestStatus.Indexing);
        var manifests = new MemoryManifestStore(v1, interruptedV2);
        await manifests.SaveAttemptAsync(Manifest("v3", _root, [], status: IndexManifestStatus.Failed));
        var graph = new MemoryGraphStore("v2", nodes: 12, edges: 18);
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle);

        var diagnostics = await service.GetDiagnosticsAsync(project.Id);

        Assert.Equal("v2", diagnostics.Current?.Version);
        Assert.Equal(12, diagnostics.Current?.NodeCount);
        Assert.Equal("v3", diagnostics.LatestAttempt?.Version);
        Assert.Equal("v2", project.IndexManifestVersion);
    }

    [Fact]
    public async Task ManifestRecovery_MarksProjectStaleWhenActiveGraphIsMissing()
    {
        var project = Project(_root, "v1");
        var current = Manifest("v1", _root, [], nodes: 4, edges: 3);
        var repository = new MemoryProjectRepository(project);
        var manifests = new MemoryManifestStore(current, current);
        var graph = new MemoryGraphStore(null);
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle);

        await service.GetDiagnosticsAsync(project.Id);

        Assert.Equal(ProjectIndexStatus.Stale, project.IndexStatus);
        Assert.Contains("Neo4j active graph", project.IndexError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullIndex_NoOpReusesCurrentGraphWithoutRunningAnalyzerOrPublisher()
    {
        var source = Path.Combine(_root, "Stable.cs");
        const string content = "namespace Stable; public class Service { }";
        await File.WriteAllTextAsync(source, content);
        var info = new FileInfo(source);
        var file = new IndexedFileManifest(
            "Stable.cs",
            "csharp",
            info.Length,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            LastWriteAt: info.LastWriteTimeUtc);
        var current = Manifest("current", _root, [file], nodes: 7, edges: 9) with
        {
            IndexerVersion = CurrentIndexerVersion(),
            GraphSchemaVersion = GraphSchemaV2.Version,
            AnalysisSnapshotHash = "stable-snapshot",
        };
        var project = Project(_root, current.Version);
        var repository = new MemoryProjectRepository(project);
        var manifests = new MemoryManifestStore(current, current);
        var graph = new MemoryGraphStore(current.Version, nodes: 7, edges: 9);
        var analyzer = new CountingAnalyzer();
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle, analyzer);

        var result = await service.IndexProjectAsync(project.Id);

        Assert.Equal(0, analyzer.CallCount);
        Assert.Equal(0, graph.ReplaceCallCount);
        Assert.Equal("current", result.IndexManifestVersion);
        Assert.Equal("no-op", service.GetLastRun(project.Id)?.Mode);
        Assert.Equal("ready", service.GetLastRun(project.Id)?.Status);
    }

    [Fact]
    public async Task FullIndex_SameLengthAndRestoredMtimeStillHashesContentAndRebuilds()
    {
        var source = Path.Combine(_root, "Changed.cs");
        const string oldContent = "class Before { }";
        const string newContent = "class After_ { }";
        Assert.Equal(Encoding.UTF8.GetByteCount(oldContent), Encoding.UTF8.GetByteCount(newContent));
        await File.WriteAllTextAsync(source, oldContent);
        var stableMtime = File.GetLastWriteTimeUtc(source);
        var info = new FileInfo(source);
        var file = new IndexedFileManifest(
            "Changed.cs",
            "csharp",
            info.Length,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldContent))).ToLowerInvariant(),
            LastWriteAt: stableMtime);
        var current = Manifest("current", _root, [file], nodes: 7, edges: 9) with
        {
            IndexerVersion = CurrentIndexerVersion(),
            GraphSchemaVersion = GraphSchemaV2.Version,
            AnalysisSnapshotHash = "old-snapshot",
        };
        await File.WriteAllTextAsync(source, newContent);
        File.SetLastWriteTimeUtc(source, stableMtime);

        var project = Project(_root, current.Version);
        var repository = new MemoryProjectRepository(project);
        var manifests = new MemoryManifestStore(current, current);
        var graph = new MemoryGraphStore(current.Version, nodes: 7, edges: 9);
        var analyzer = new CountingAnalyzer();
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle, analyzer);

        await service.IndexProjectAsync(project.Id);

        Assert.Equal(1, analyzer.CallCount);
        Assert.Equal(1, graph.ReplaceCallCount);
        Assert.Equal("full", service.GetLastRun(project.Id)?.Mode);
    }

    [Fact]
    public async Task FullIndex_PartialManifestIsRetriedInsteadOfPermanentlyNoOp()
    {
        var source = Path.Combine(_root, "Retry.cs");
        const string content = "class Retry { }";
        await File.WriteAllTextAsync(source, content);
        var info = new FileInfo(source);
        var current = Manifest(
            "partial",
            _root,
            [new IndexedFileManifest(
                "Retry.cs",
                "csharp",
                info.Length,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                LastWriteAt: info.LastWriteTimeUtc)],
            status: IndexManifestStatus.Partial,
            nodes: 3,
            edges: 2) with
        {
            IndexerVersion = CurrentIndexerVersion(),
            GraphSchemaVersion = GraphSchemaV2.Version,
            AnalysisSnapshotHash = "partial-snapshot",
            RequiresRetry = true,
        };
        var project = Project(_root, current.Version);
        var repository = new MemoryProjectRepository(project);
        var manifests = new MemoryManifestStore(current, current);
        var graph = new MemoryGraphStore(current.Version, nodes: 3, edges: 2);
        var analyzer = new CountingAnalyzer();
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle, analyzer);

        await service.IndexProjectAsync(project.Id);

        Assert.Equal(1, analyzer.CallCount);
        Assert.Equal(1, graph.ReplaceCallCount);
        Assert.Equal("full", service.GetLastRun(project.Id)?.Mode);
    }

    [Fact]
    public async Task FullIndex_DeterministicPartialManifestCanNoOpAndRemainsPartial()
    {
        var source = Path.Combine(_root, "StableWarning.cs");
        const string content = "class StableWarning { }";
        await File.WriteAllTextAsync(source, content);
        var info = new FileInfo(source);
        var current = Manifest(
            "partial-warning",
            _root,
            [new IndexedFileManifest(
                "StableWarning.cs",
                "csharp",
                info.Length,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                LastWriteAt: info.LastWriteTimeUtc)],
            status: IndexManifestStatus.Partial,
            nodes: 3,
            edges: 2) with
        {
            IndexerVersion = CurrentIndexerVersion(),
            GraphSchemaVersion = GraphSchemaV2.Version,
            AnalysisSnapshotHash = "partial-warning-snapshot",
            Error = "deterministic unsupported SQL warning",
            RequiresRetry = false,
        };
        var project = Project(_root, current.Version);
        var repository = new MemoryProjectRepository(project);
        var manifests = new MemoryManifestStore(current, current);
        var graph = new MemoryGraphStore(current.Version, nodes: 3, edges: 2);
        var analyzer = new CountingAnalyzer();
        await using var lifecycle = Lifecycle(graph);
        var service = Service(repository, manifests, graph, lifecycle, analyzer);

        var indexed = await service.IndexProjectAsync(project.Id);

        Assert.Equal(0, analyzer.CallCount);
        Assert.Equal(0, graph.ReplaceCallCount);
        Assert.Equal("no-op", service.GetLastRun(project.Id)?.Mode);
        Assert.Equal(ProjectIndexStatus.Partial, indexed.IndexStatus);
        Assert.Equal(current.Version, indexed.IndexManifestVersion);
        Assert.Equal(IndexManifestStatus.Partial, manifests.Latest?.Status);
        Assert.Equal(current.Error, manifests.Latest?.Error);
        Assert.Equal(false, manifests.Latest?.RequiresRetry);
    }

    [Fact]
    public void GitChangeDetection_CoversStagedUntrackedAndRenameDestinations()
    {
        Assert.True(CanRunGit(), "git CLI is required for project change detection acceptance tests.");

        RunGit("init");
        RunGit("config", "user.name", "Wingman Test");
        RunGit("config", "user.email", "wingman-test@example.invalid");
        File.WriteAllText(Path.Combine(_root, "Modified.cs"), "class Before { }");
        File.WriteAllText(Path.Combine(_root, "RenameFrom.java"), "class RenameFrom { }");
        RunGit("add", "--all");
        RunGit("commit", "-m", "baseline");

        File.WriteAllText(Path.Combine(_root, "Modified.cs"), "class After { }");
        RunGit("add", "Modified.cs");
        File.Move(Path.Combine(_root, "RenameFrom.java"), Path.Combine(_root, "RenameTo.java"));
        RunGit("add", "--all");
        File.WriteAllText(Path.Combine(_root, "Untracked.cs"), "class Untracked { }");

        var changed = ProjectIndexService.GetGitChangedFiles(_root);

        Assert.Contains("Modified.cs", changed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("RenameTo.java", changed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Untracked.cs", changed, StringComparer.OrdinalIgnoreCase);
    }

    private ProjectIndexService Service(
        IProjectRepository repository,
        IProjectIndexManifestStore manifests,
        ICodeGraphStore graph,
        Neo4jLifecycleService lifecycle,
        ICodeAnalyzer? analyzer = null) => new(
        [analyzer ?? new EmptyAnalyzer()], graph, repository, manifests, new EmptyDataExtractor(),
        new EmptyGlossaryStore(), lifecycle, NullLogger<ProjectIndexService>.Instance,
        Options.Create(new ProjectIndexOptimizationOptions()));

    private static Neo4jLifecycleService Lifecycle(ICodeGraphStore graph) => new(
        Options.Create(new Neo4jLifecycleOptions { Mode = "external" }),
        Options.Create(new Neo4jOptions()),
        graph,
        new NoopHttpClientFactory(),
        NullLogger<Neo4jLifecycleService>.Instance);

    private static ProjectEntity Project(string root, string version) => new()
    {
        Id = "project-1",
        Name = "Project",
        RootPath = root,
        IndexStatus = ProjectIndexStatus.Indexed,
        IndexManifestVersion = version,
    };

    private static ProjectIndexManifest Manifest(
        string version,
        string root,
        IReadOnlyList<IndexedFileManifest> files,
        IndexManifestStatus status = IndexManifestStatus.Fresh,
        int nodes = 0,
        int edges = 0) => new(
        "project-1", version, root, "head", "fingerprint", [], files, [], "test",
        DateTimeOffset.UtcNow.AddMinutes(-1),
        status == IndexManifestStatus.Indexing ? null : DateTimeOffset.UtcNow,
        status,
        nodes,
        edges);

    private static string CurrentIndexerVersion() =>
        typeof(ProjectIndexService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ProjectIndexService).Assembly.GetName().Version?.ToString()
        ?? "development";

    private static IndexedFileManifest FileManifest(string root, string path, string oldContent) => new(
        Path.GetRelativePath(root, path).Replace('\\', '/'),
        Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? "csharp" : "config",
        Encoding.UTF8.GetByteCount(oldContent),
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldContent))).ToLowerInvariant(),
        LastWriteAt: DateTimeOffset.UnixEpoch);

    private bool CanRunGit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return process is not null && process.WaitForExit(5_000) && process.ExitCode == 0;
        }
        catch { return false; }
    }

    private void RunGit(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("git process could not start");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private sealed class EmptyAnalyzer : ICodeAnalyzer
    {
        public string Language => "csharp";
        public IReadOnlyList<string> FileExtensions => [".cs"];
        public Task<CodeAnalysisResult> AnalyzeAsync(string projectRoot, IReadOnlyList<string> files, CancellationToken ct = default) =>
            Task.FromResult(new CodeAnalysisResult());
    }

    private sealed class CountingAnalyzer : ICodeAnalyzer
    {
        public int CallCount { get; private set; }
        public string Language => "csharp";
        public IReadOnlyList<string> FileExtensions => [".cs"];
        public Task<CodeAnalysisResult> AnalyzeAsync(
            string projectRoot, IReadOnlyList<string> files, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new CodeAnalysisResult());
        }
    }

    private sealed class EmptyDataExtractor : IDataSchemaExtractor
    {
        public Task<DataExtractionResult> ExtractAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DataExtractionResult(new CodeAnalysisResult(), [], []));
    }
}

public sealed class DataIntelligenceAcceptanceTests
{
    [Fact]
    public async Task DataExtractor_ContentHashUsesOriginalBytesAndDecodesBomAwareText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wingman-data-hash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "schema.sql");
            var preamble = Encoding.UTF8.GetPreamble();
            var content = Encoding.UTF8.GetBytes("CREATE TABLE dbo.Items (Id int PRIMARY KEY);");
            var bytes = preamble.Concat(content).ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            var extractor = new DataSchemaExtractor([new SqlDataArtifactAdapter()]);

            var result = await extractor.ExtractAsync(root);

            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.NotEmpty(result.Graph.Nodes);
            Assert.All(result.Graph.Nodes, node => Assert.Equal(expected, node.ContentHash));
            Assert.All(result.Graph.Edges, edge => Assert.Equal(expected, edge.ContentHash));
            Assert.Equal(expected, Assert.Single(result.ScannedFiles!).ContentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SqlExtractor_ProducesTraceableMigrationSchemaAndReadWriteGraph()
    {
        const string sql = """
            CREATE TABLE sales.Orders (
                Id bigint PRIMARY KEY,
                CustomerId bigint,
                CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES sales.Customers(Id)
            );
            SELECT Id, CustomerId FROM sales.Orders;
            UPDATE sales.Orders SET CustomerId = @customerId WHERE Id = @id;
            """;
        var artifact = new DataArtifact(
            "V001__orders.sql", "db/V001__orders.sql", sql,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant());

        var result = new SqlDataArtifactAdapter().Analyze(artifact).Graph;

        var table = Assert.Single(result.Nodes, node =>
            node.Kind == CodeNodeKind.Table &&
            node.Signature == "sales.Orders" &&
            node.SourceKind == GraphSourceKind.Migration &&
            node.Confidence == GraphConfidence.Exact);
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.Column && node.Name == "CustomerId");
        Assert.Contains(result.Nodes, node => node.Kind == CodeNodeKind.ForeignKey);
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Migrates && edge.TargetKey == table.Key);
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Reads && edge.TargetKey == table.Key);
        Assert.Contains(result.Edges, edge => edge.Kind == CodeEdgeKind.Writes && edge.TargetKey == table.Key);
        Assert.All(result.Nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.ContentHash)));
    }

    [Fact]
    public async Task Glossary_OnlyConfirmedProposalBecomesConfirmedKnowledgeAndRetainsEvidenceKeys()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var factory = new ContextFactory(options);
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Projects.Add(new ProjectRecord
            {
                Id = "project-1", Name = "Project", RootPath = "C:/repo", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var store = new DomainGlossarySqliteStore(factory);
        var proposed = await store.ProposeAsync("project-1", new ProposeGlossaryEntryRequest(
            "訂單", "客戶已提交的購買要求", ["Order"], GlossarySensitivity.Internal,
            ["table:sales.orders"], "agent"));

        Assert.Empty(await store.ListAsync("project-1", GlossaryProposalStatus.Confirmed));
        var confirmed = await store.ReviewAsync("project-1", proposed.Id, new ReviewGlossaryEntryRequest(
            true, "IT", Definition: "經 IT 確認的訂單定義", Comment: "已核對 schema"));

        Assert.Equal(GlossaryProposalStatus.Confirmed, confirmed.Status);
        Assert.Equal(["table:sales.orders"], confirmed.EvidenceKeys);
        Assert.Single(await store.ListAsync("project-1", GlossaryProposalStatus.Confirmed));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReviewAsync("project-1", proposed.Id, new ReviewGlossaryEntryRequest(false, "IT")));
    }

    [Fact]
    public async Task RuntimeProvider_DoesNotInvokePluginScopedToAnotherProject()
    {
        var runtime = new CapturingMcpRuntime(SafeRuntimeOutput());
        var provider = RuntimeProvider(
            new McpServerDefinition(1, "database", McpTransport.Stdio, "db", [], null,
                new Dictionary<string, string> { ["WINGMAN_PROJECT_ID"] = "project-2" }, true),
            runtime);

        var evidence = await provider.FindConfigurationAsync(
            "project-1", new DatabaseConfigurationLookup(Key: "feature.checkout", MaxResults: 1));

        Assert.Empty(evidence);
        Assert.Equal(0, runtime.CallCount);
    }

    [Fact]
    public async Task RuntimeProvider_RejectsForbiddenRawValueAtAnyNestedDepth()
    {
        var unsafeOutput = """
            {"subject":"feature.checkout","state":"Enabled","redaction":"DerivedOnly","metadata":{"nested":{"value":"must-not-leak"}}}
            """;
        var runtime = new CapturingMcpRuntime(unsafeOutput);
        var provider = RuntimeProvider(
            new McpServerDefinition(1, "database", McpTransport.Stdio, "db", [], null,
                new Dictionary<string, string> { ["WINGMAN_PROJECT_ID"] = "project-1" }, true),
            runtime);

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.FindConfigurationAsync(
            "project-1", new DatabaseConfigurationLookup(Key: "feature.checkout", MaxResults: 1)));
        Assert.Equal(1, runtime.CallCount);
    }

    private static McpDatabaseRuntimeEvidenceProvider RuntimeProvider(
        McpServerDefinition server,
        CapturingMcpRuntime runtime) => new(
        [new FixedPluginSource(server)],
        runtime,
        new MemoryProjectRepository(new ProjectEntity
        {
            Id = "project-1", Name = "Project", RootPath = Path.GetTempPath(),
        }),
        new DatabaseRuntimeEvidenceRequestValidator(),
        new StrictReadOnlyDatabaseQueryPlanValidator());

    private static string SafeRuntimeOutput() => JsonSerializer.Serialize(new
    {
        subject = "feature.checkout",
        state = "Enabled",
        redaction = "DerivedOnly",
        observedAt = DateTimeOffset.UtcNow,
        expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
    });

    private sealed class FixedPluginSource(McpServerDefinition server) : IPluginMcpServerSource
    {
        public Task<IReadOnlyList<McpServerDefinition>> ListEnabledAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpServerDefinition>>([server]);
    }

    private sealed class CapturingMcpRuntime(string output) : IMcpClientRuntime
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(
            McpServerDefinition server, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>>
            ([new McpToolDefinition(server.Id, server.Name, "find_configuration", null,
                JsonSerializer.SerializeToElement(new { type = "object" }), true)]);

        public Task<McpCallResult> CallToolAsync(
            McpServerDefinition server, string toolName, JsonElement arguments, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new McpCallResult(true, output));
        }
    }

    private sealed class ContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}

public sealed class ProjectCodeGraphSnapshotAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wingman-graph-snapshot-" + Guid.NewGuid().ToString("N"));

    public ProjectCodeGraphSnapshotAcceptanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CSharp_InterfaceDispatchSnapshot_RemainsStableAndTraceable()
    {
        var path = Path.Combine(_root, "Handler.cs");
        await File.WriteAllTextAsync(path, """
            namespace Acme;
            public interface IHandler { void Handle(); }
            public sealed class Handler : IHandler { public void Handle() { } }
            """);

        var graph = await new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance)
            .AnalyzeAsync(_root, [path]);

        var expected = """
            N|Method|Acme.Handler.Handle()
            N|Method|Acme.Handler.Handler()
            N|Method|Acme.IHandler.Handle()
            N|Type|Acme.Handler
            N|Type|Acme.IHandler
            E|Acme.Handler.Handle()|Implements|Acme.IHandler.Handle()
            E|Acme.Handler|Implements|Acme.IHandler
            E|Acme.IHandler.Handle()|DispatchesTo|Acme.Handler.Handle()
            """.Replace("\r\n", "\n");
        var actual = DispatchSnapshot(graph, "Acme.");
        Assert.True(expected == actual, $"C# dispatch snapshot changed:\n{actual}");
    }

    [Fact]
    public async Task Java_InterfaceDispatchSnapshot_RemainsStableAndTraceable()
    {
        var path = Path.Combine(_root, "Handler.java");
        await File.WriteAllTextAsync(path, """
            package acme;
            interface HandlerContract { void handle(); }
            final class Handler implements HandlerContract { public void handle() { } }
            """);

        var graph = await new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance)
            .AnalyzeAsync(_root, [path]);

        var expected = """
            N|Method|acme.Handler.handle()
            N|Method|acme.HandlerContract.handle()
            N|Type|acme.Handler
            N|Type|acme.HandlerContract
            E|acme.Handler.handle()|Implements|acme.HandlerContract.handle()
            E|acme.HandlerContract.handle()|DispatchesTo|acme.Handler.handle()
            E|acme.Handler|Implements|acme.HandlerContract
            """.Replace("\r\n", "\n");
        var actual = DispatchSnapshot(graph, "acme.");
        Assert.True(expected == actual, $"Java dispatch snapshot changed:\n{actual}");
    }

    private static string DispatchSnapshot(CodeAnalysisResult graph, string prefix)
    {
        var nodes = graph.Nodes
            .Where(node => node.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                           node.Kind is CodeNodeKind.Type or CodeNodeKind.Method)
            .Select(node => $"N|{node.Kind}|{node.Key}");
        var edges = graph.Edges
            .Where(edge => edge.SourceKey.StartsWith(prefix, StringComparison.Ordinal) &&
                           edge.TargetKey.StartsWith(prefix, StringComparison.Ordinal) &&
                           edge.Kind is CodeEdgeKind.Implements or CodeEdgeKind.DispatchesTo)
            .Select(edge => $"E|{edge.SourceKey}|{edge.Kind}|{edge.TargetKey}");
        return string.Join('\n',
            nodes.OrderBy(line => line, StringComparer.Ordinal)
                .Concat(edges.OrderBy(line => line, StringComparer.Ordinal)));
    }
}

/// <summary>
/// This is a real Neo4j contract test, not an in-memory substitute. CI may opt in by
/// setting WINGMAN_TEST_NEO4J_URI (and optional username/password/database variables).
/// </summary>
[Collection("External index acceptance")]
public sealed class OptionalNeo4jProjectGraphAcceptanceTests
{
    [Neo4jFact]
    public async Task ReplaceDepthAndTruncation_WorkAgainstRealNeo4j()
    {
        var uri = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_URI");
        Assert.False(string.IsNullOrWhiteSpace(uri));

        var options = Options.Create(new Neo4jOptions
        {
            Uri = uri,
            Username = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_USERNAME") ?? "neo4j",
            Password = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_PASSWORD") ?? "test-only-neo4j-password",
            Database = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_DATABASE") ?? "neo4j",
        });
        await using var store = new Neo4jCodeGraphStore(
            options, NullLogger<Neo4jCodeGraphStore>.Instance);
        Assert.True(await store.PingAsync(), $"Neo4j is not reachable at {uri}.");
        await store.EnsureSchemaAsync();

        var projectId = "acceptance-" + Guid.NewGuid().ToString("N");
        try
        {
            var graph = new CodeAnalysisResult();
            foreach (var key in new[] { "chain:A", "chain:B", "chain:C", "hub" })
                graph.Nodes.Add(Node(key));
            graph.Edges.Add(Edge("chain:A", "chain:B"));
            graph.Edges.Add(Edge("chain:B", "chain:C"));
            for (var index = 0; index < 101; index++)
            {
                var key = $"leaf:{index:D3}";
                graph.Nodes.Add(Node(key));
                graph.Edges.Add(Edge("hub", key));
            }

            await store.ReplaceProjectAsync(
                projectId,
                Descriptor("manifest-acceptance", graph),
                graph);

            Assert.Equal("manifest-acceptance", await store.GetProjectManifestVersionAsync(projectId));
            var depthTwo = await store.GetNeighborhoodAsync(projectId, "chain:A", 2);
            Assert.Equal(2, depthTwo.Depth);
            Assert.Equal("manifest-acceptance", depthTwo.Center?.ManifestVersion);
            Assert.All(depthTwo.Neighbors, neighbor =>
                Assert.Equal("manifest-acceptance", neighbor.ManifestVersion));
            Assert.Contains(depthTwo.Neighbors, neighbor => neighbor.Key == "chain:C");
            var bounded = await store.GetNeighborhoodAsync(projectId, "hub", 1);
            Assert.True(bounded.Truncated);
            Assert.Equal(100, bounded.Neighbors.Count);
            var activeStorage = await store.QueryVisualGraphAsync(
                projectId,
                """
                MATCH (n:CodeNode {projectId: $projectId})
                RETURN n.manifestVersion AS entityVersion, n.graphId AS graphId
                LIMIT 1
                """);
            var activeRow = Assert.Single(activeStorage.Rows);
            Assert.Null(activeRow["entityVersion"]);
            Assert.Equal("manifest-acceptance", activeRow["graphId"]);
            var provenanceStorage = await store.QueryVisualGraphAsync(
                projectId,
                """
                MATCH (source:CodeNode {projectId: $projectId, key: 'chain:A'})
                      -[relationship:CALLS]->
                      (target:CodeNode {projectId: $projectId, key: 'chain:B'})
                RETURN source.locationsJson AS locationsJson,
                       source.evidenceJson AS nodeEvidenceJson,
                       relationship.evidenceJson AS edgeEvidenceJson
                """);
            var provenanceRow = Assert.Single(provenanceStorage.Rows);
            Assert.Equal("[{\"artifactId\":\"file:test.cs\"}]", provenanceRow["locationsJson"]);
            Assert.Equal("[{\"extractor\":\"acceptance\"}]", provenanceRow["nodeEvidenceJson"]);
            Assert.Equal("[{\"extractor\":\"acceptance-edge\"}]", provenanceRow["edgeEvidenceJson"]);

            var deltaDescriptor = new GraphPublishDescriptor(
                "manifest-delta", "graph-delta", "2.0", "snapshot-delta", 105, 102);
            await store.ApplyProjectDeltaAsync(
                projectId,
                deltaDescriptor,
                new GraphPublishDelta(
                    "manifest-acceptance",
                    ["leaf:000"],
                    [Node("chain:B", "chain:B updated"), Node("chain:D")],
                    [new GraphEdgeIdentity("chain:B", CodeEdgeKind.Calls, "chain:C")],
                    [Edge("chain:C", "chain:D", "[{\"extractor\":\"delta-edge\"}]")]));

            Assert.Equal("manifest-delta", await store.GetProjectManifestVersionAsync(projectId));
            Assert.Equal((105, 102), await store.GetStatsAsync(projectId));
            Assert.Null((await store.GetNeighborhoodAsync(projectId, "leaf:000", 1)).Center);
            var updated = await store.GetNeighborhoodAsync(projectId, "chain:B", 1);
            Assert.Equal("chain:B updated", updated.Center?.Name);
            // The unchanged inbound caller must survive clone + node upsert.
            Assert.Contains(updated.Neighbors, neighbor =>
                neighbor.Key == "chain:A" &&
                neighbor.RelationKind == "CALLS" &&
                neighbor.Direction == "in");
            Assert.DoesNotContain(updated.Neighbors, neighbor => neighbor.Key == "chain:C");
            var deltaProvenance = await store.QueryVisualGraphAsync(
                projectId,
                """
                MATCH (source:CodeNode {projectId: $projectId, key: 'chain:C'})
                      -[relationship:CALLS]->
                      (target:CodeNode {projectId: $projectId, key: 'chain:D'})
                RETURN source.graphId AS sourceGraphId,
                       source.manifestVersion AS entityManifestVersion,
                       relationship.graphId AS relationshipGraphId,
                       relationship.evidenceJson AS evidenceJson
                """);
            var deltaRow = Assert.Single(deltaProvenance.Rows);
            Assert.Equal("graph-delta", deltaRow["sourceGraphId"]);
            Assert.Null(deltaRow["entityManifestVersion"]);
            Assert.Equal("graph-delta", deltaRow["relationshipGraphId"]);
            Assert.Equal("[{\"extractor\":\"delta-edge\"}]", deltaRow["evidenceJson"]);
            var clonedProvenance = await store.QueryVisualGraphAsync(
                projectId,
                """
                MATCH (:CodeNode {projectId: $projectId, key: 'chain:A'})
                      -[relationship:CALLS]->
                      (:CodeNode {projectId: $projectId, key: 'chain:B'})
                RETURN relationship.evidenceJson AS evidenceJson
                """);
            Assert.Equal(
                "[{\"extractor\":\"acceptance-edge\"}]",
                Assert.Single(clonedProvenance.Rows)["evidenceJson"]);

            // A dangling edge makes staged validation fail. The previous success must
            // remain the only active graph and the run-owned stage must be cleaned.
            var failedDescriptor = new GraphPublishDescriptor(
                "manifest-failed-delta", "graph-failed-delta", "2.0", "snapshot-failed", 105, 103);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ApplyProjectDeltaAsync(
                    projectId,
                    failedDescriptor,
                    new GraphPublishDelta(
                        "manifest-delta",
                        [],
                        [],
                        [],
                        [Edge("chain:D", "missing:target")])));
            Assert.Equal("manifest-delta", await store.GetProjectManifestVersionAsync(projectId));
            Assert.Equal((105, 102), await store.GetStatsAsync(projectId));
            Assert.NotNull((await store.GetNeighborhoodAsync(projectId, "chain:A", 1)).Center);

            var replacement = new CodeAnalysisResult();
            replacement.Nodes.Add(Node("replacement:only"));
            await store.ReplaceProjectAsync(
                projectId,
                Descriptor("manifest-replacement", replacement),
                replacement);

            Assert.Equal("manifest-replacement", await store.GetProjectManifestVersionAsync(projectId));
            Assert.Equal((1, 0), await store.GetStatsAsync(projectId));
            Assert.Null((await store.GetNeighborhoodAsync(projectId, "chain:A", 1)).Center);
        }
        finally
        {
            await store.DeleteProjectAsync(projectId);
        }
    }

    [Neo4jFact]
    public async Task ReverseImpactClosure_CoversDispatchAndDataEdges()
    {
        var uri = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_URI");
        Assert.False(string.IsNullOrWhiteSpace(uri));
        var options = Options.Create(new Neo4jOptions
        {
            Uri = uri,
            Username = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_USERNAME") ?? "neo4j",
            Password = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_PASSWORD") ?? "test-only-neo4j-password",
            Database = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_DATABASE") ?? "neo4j",
        });
        await using var store = new Neo4jCodeGraphStore(
            options, NullLogger<Neo4jCodeGraphStore>.Instance);
        var projectId = "impact-" + Guid.NewGuid().ToString("N");
        try
        {
            var graph = new CodeAnalysisResult();
            graph.Nodes.AddRange(
            [
                Node("Caller.Run()"),
                Node("IService.Execute()"),
                Node("Service.Execute()"),
                Node("query:orders", kind: CodeNodeKind.Query),
                Node("table:sales.orders", kind: CodeNodeKind.Table),
            ]);
            graph.Edges.Add(Edge("Caller.Run()", "IService.Execute()"));
            graph.Edges.Add(Edge(
                "IService.Execute()", "Service.Execute()", kind: CodeEdgeKind.DispatchesTo));
            graph.Edges.Add(Edge(
                "Service.Execute()", "query:orders", kind: CodeEdgeKind.Contains));
            graph.Edges.Add(Edge(
                "query:orders", "table:sales.orders", kind: CodeEdgeKind.Reads));
            await store.ReplaceProjectAsync(projectId, Descriptor("impact-v1", graph), graph);

            var implementationImpact = await store.GetReverseCallChainAsync(
                projectId, "Service.Execute()", 3);
            Assert.Contains(implementationImpact, path =>
                path.Chain.Select(node => node.Key).SequenceEqual(
                    ["Caller.Run()", "IService.Execute()", "Service.Execute()"]));

            var dataImpact = await store.GetReverseCallChainAsync(
                projectId, "table:sales.orders", 6);
            Assert.Contains(dataImpact, path =>
                path.Chain.First().Key == "Caller.Run()" &&
                path.Chain.Last().Key == "table:sales.orders");
        }
        finally
        {
            await store.DeleteProjectAsync(projectId);
        }
    }

    private static CodeNode Node(
        string key,
        string? name = null,
        CodeNodeKind kind = CodeNodeKind.Method) => new()
    {
        Key = key,
        Kind = kind,
        Name = name ?? key,
        Language = "test",
        SourceKind = GraphSourceKind.Ast,
        Confidence = GraphConfidence.Exact,
        ExtractorId = "acceptance",
        ExtractorVersion = "1",
        IndexedAt = DateTimeOffset.UtcNow,
        ContentHash = "acceptance",
        LocationsJson = "[{\"artifactId\":\"file:test.cs\"}]",
        EvidenceJson = "[{\"extractor\":\"acceptance\"}]",
    };

    private static GraphPublishDescriptor Descriptor(string version, CodeAnalysisResult graph) =>
        new(version, version, "2.0", $"snapshot-{version}", graph.Nodes.Count, graph.Edges.Count);

    private static CodeEdge Edge(
        string source,
        string target,
        string evidenceJson = "[{\"extractor\":\"acceptance-edge\"}]",
        CodeEdgeKind kind = CodeEdgeKind.Calls) => new()
    {
        SourceKey = source,
        TargetKey = target,
        Kind = kind,
        SourceKind = GraphSourceKind.Ast,
        Confidence = GraphConfidence.Exact,
        ExtractorId = "acceptance",
        ExtractorVersion = "1",
        IndexedAt = DateTimeOffset.UtcNow,
        ContentHash = "acceptance",
        EvidenceJson = evidenceJson,
    };
}

internal sealed class Neo4jFactAttribute : FactAttribute
{
    public Neo4jFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_URI")))
            Skip = "Set WINGMAN_TEST_NEO4J_URI to run the real Neo4j graph acceptance test.";
    }
}

internal sealed class MemoryProjectRepository(params ProjectEntity[] projects) : IProjectRepository
{
    private readonly Dictionary<string, ProjectEntity> _projects = projects.ToDictionary(project => project.Id);
    public Task<IReadOnlyList<ProjectEntity>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProjectEntity>>(_projects.Values.ToList());
    public Task<ProjectEntity?> GetAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_projects.GetValueOrDefault(projectId));
    public Task SaveAsync(ProjectEntity project, CancellationToken ct = default)
    {
        _projects[project.Id] = project;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string projectId, CancellationToken ct = default)
    {
        _projects.Remove(projectId);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryManifestStore(
    ProjectIndexManifest? current,
    ProjectIndexManifest? latest) : IProjectIndexManifestStore
{
    private readonly Dictionary<string, ProjectIndexManifest> _versions = new[] { current, latest }
        .Where(manifest => manifest is not null)
        .Select(manifest => manifest!)
        .GroupBy(manifest => manifest.Version, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    public ProjectIndexManifest? Current { get; private set; } = current;
    public ProjectIndexManifest? Latest { get; private set; } = latest;
    public Task SaveAttemptAsync(ProjectIndexManifest manifest, CancellationToken ct = default)
    {
        _versions[manifest.Version] = manifest;
        Latest = manifest;
        return Task.CompletedTask;
    }
    public Task PromoteAsync(ProjectIndexManifest manifest, CancellationToken ct = default)
    {
        _versions[manifest.Version] = manifest;
        Current = manifest;
        if (Latest is null || Latest.StartedAt <= manifest.StartedAt)
            Latest = manifest;
        return Task.CompletedTask;
    }
    public Task<ProjectIndexManifest?> GetCurrentAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(Current);
    public Task<ProjectIndexManifest?> GetLatestAttemptAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(Latest);
    public Task<ProjectIndexManifest?> GetByVersionAsync(string projectId, string version, CancellationToken ct = default) =>
        Task.FromResult(_versions.TryGetValue(version, out var manifest) &&
            string.Equals(manifest.ProjectId, projectId, StringComparison.Ordinal)
                ? manifest
                : null);
    public Task DeleteProjectAsync(string projectId, CancellationToken ct = default)
    {
        Current = Latest = null;
        return Task.CompletedTask;
    }
}

internal sealed class EmptyGlossaryStore : IDomainGlossaryStore
{
    public Task<IReadOnlyList<DomainGlossaryEntry>> ListAsync(string projectId, GlossaryProposalStatus? status = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DomainGlossaryEntry>>([]);
    public Task<DomainGlossaryEntry?> GetAsync(string projectId, string id, CancellationToken cancellationToken = default) => Task.FromResult<DomainGlossaryEntry?>(null);
    public Task<DomainGlossaryEntry> ProposeAsync(string projectId, ProposeGlossaryEntryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DomainGlossaryEntry> ReviewAsync(string projectId, string id, ReviewGlossaryEntryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class MemoryGraphStore(string? version, int nodes = 0, int edges = 0) : ICodeGraphStore
{
    public int ReplaceCallCount { get; private set; }
    public Func<string, string, int, CancellationToken, Task<IReadOnlyList<GraphSearchHit>>>? SearchHandler { get; init; }
    public Func<string, string, int, CancellationToken, Task<GraphNeighborhood>>? NeighborhoodHandler { get; init; }
    public Func<string, string, int, CancellationToken, Task<IReadOnlyList<ImpactPath>>>? ReverseCallChainHandler { get; init; }
    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ReplaceProjectAsync(string projectId, GraphPublishDescriptor descriptor, CodeAnalysisResult result, CancellationToken ct = default)
    {
        ReplaceCallCount++;
        return Task.CompletedTask;
    }
    public Task<string?> GetProjectManifestVersionAsync(string projectId, CancellationToken ct = default) => Task.FromResult(version);
    public Task DeleteProjectAsync(string projectId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<GraphSearchHit>> SearchAsync(string projectId, string query, int limit = 20, CancellationToken ct = default) =>
        SearchHandler?.Invoke(projectId, query, limit, ct) ?? Task.FromResult<IReadOnlyList<GraphSearchHit>>([]);
    public Task<GraphNeighborhood> GetNeighborhoodAsync(string projectId, string nodeKey, int depth = 1, CancellationToken ct = default) =>
        NeighborhoodHandler?.Invoke(projectId, nodeKey, depth, ct) ?? Task.FromResult(new GraphNeighborhood(null, [], false, depth));
    public Task<IReadOnlyList<ImpactPath>> GetReverseCallChainAsync(string projectId, string nodeKey, int maxDepth = 3, CancellationToken ct = default) =>
        ReverseCallChainHandler?.Invoke(projectId, nodeKey, maxDepth, ct) ?? Task.FromResult<IReadOnlyList<ImpactPath>>([]);
    public Task<(int Nodes, int Edges)> GetStatsAsync(string projectId, CancellationToken ct = default) => Task.FromResult((nodes, edges));
    public Task<IReadOnlyList<GraphSearchHit>> GetCentralNodesAsync(string projectId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GraphSearchHit>>([]);
    public Task SaveCommunitySummaryAsync(string projectId, string targetManifestVersion, string communityId, string title, string summary, IReadOnlyList<string> memberKeys, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<CommunitySummary>> ListCommunitySummariesAsync(string projectId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CommunitySummary>>([]);
    public Task<IReadOnlyDictionary<string, string>> DetectCommunitiesAsync(string projectId, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    public Task<CodeGraphVisualData> GetVisualGraphAsync(string projectId, int limit = 1000, IReadOnlyList<string>? kinds = null, IReadOnlyList<string>? relationTypes = null, CancellationToken ct = default) => Task.FromResult(new CodeGraphVisualData([], [], 0, 0, 0, false));
    public Task<CodeGraphSchema> GetVisualSchemaAsync(string projectId, CancellationToken ct = default) => Task.FromResult(new CodeGraphSchema(0, 0, [], [], []));
    public Task<CodeGraphQueryResult> QueryVisualGraphAsync(string projectId, string cypher, int limit = 1000, CancellationToken ct = default) => Task.FromResult(new CodeGraphQueryResult([], [], new CodeGraphVisualData([], [], 0, 0, 0, false)));
    public Task<CodeGraphVisualData> GetVisualNeighborsAsync(string projectId, IReadOnlyList<string> nodeKeys, int depth = 1, int limit = 1000, string mode = "all", CancellationToken ct = default) => Task.FromResult(new CodeGraphVisualData([], [], 0, 0, 0, false));
}

internal sealed class NoopHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
