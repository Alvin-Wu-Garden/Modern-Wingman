using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;
using AgentService.Infrastructure.CodeAnalysis;
using AgentService.Infrastructure.CodeGraph;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace AgentService.UnitTests;

/// <summary>
/// Explicitly opt-in: this test never downloads nopCommerce and configures the
/// lifecycle in external mode, so it cannot install or start Neo4j.
/// </summary>
[Collection("External index acceptance")]
public sealed class OptionalNopCommerceIndexBenchmarkTests(ITestOutputHelper output)
{
    private const string RootVariable = "WINGMAN_BENCHMARK_NOPCOMMERCE_ROOT";
    private const string UriVariable = "WINGMAN_BENCHMARK_NEO4J_URI";

    [NopCommerceBenchmarkFact]
    [Trait("Category", "ExternalBenchmark")]
    public async Task WarmFullIndex_ProducesStableGraphAndMeetsConfiguredP95()
    {
        var root = Path.GetFullPath(RequiredEnvironment(RootVariable));
        ValidateNopCommerceFixture(root);
        var neo4j = Neo4jConfiguration();
        var warmups = BoundedInteger("WINGMAN_BENCHMARK_WARMUP_RUNS", 1, 0, 10);
        var measured = BoundedInteger("WINGMAN_BENCHMARK_MEASURED_RUNS", 5, 1, 50);
        var p95Limit = BoundedInteger("WINGMAN_BENCHMARK_FULL_P95_LIMIT_MS", 60_000, 1, 900_000);
        var expectedNodes = OptionalInteger("WINGMAN_BENCHMARK_EXPECTED_NODES");
        var expectedEdges = OptionalInteger("WINGMAN_BENCHMARK_EXPECTED_EDGES");
        var expectedHash = Environment.GetEnvironmentVariable("WINGMAN_BENCHMARK_EXPECTED_SNAPSHOT_HASH");

        var (fixtureFingerprint, fingerprintedFiles) = ComputeFixtureFingerprint(root);
        var fixture = new IndexBenchmarkFixture(
            "nopCommerce",
            "sha256(relative-path\\0length\\0content-sha256\\n)/v1",
            fixtureFingerprint,
            fingerprintedFiles);

        await using var graphStore = new Neo4jCodeGraphStore(
            Options.Create(neo4j), NullLogger<Neo4jCodeGraphStore>.Instance);
        Assert.True(await graphStore.PingAsync(), $"External Neo4j is not reachable at {neo4j.Uri}.");
        await graphStore.EnsureSchemaAsync();

        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var dbFactory = new BenchmarkDbContextFactory(dbOptions);
        await using (var db = new AppDbContext(dbOptions))
        {
            await db.Database.EnsureCreatedAsync();
            await AgentSchemaMigrator.ApplyAsync(db);
        }

        var repository = new ProjectRepository(dbFactory);
        var manifests = new ProjectIndexManifestStore(dbFactory);
        var glossary = new DomainGlossarySqliteStore(dbFactory);
        await using var lifecycle = new Neo4jLifecycleService(
            Options.Create(new Neo4jLifecycleOptions { Mode = "external" }),
            Options.Create(neo4j),
            graphStore,
            new BenchmarkHttpClientFactory(),
            NullLogger<Neo4jLifecycleService>.Instance);
        var optimization = new ProjectIndexOptimizationOptions
        {
            EnableNoOpFastPath = false,
            ForceFullIndex = true,
        };
        var service = new ProjectIndexService(
            [
                new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance),
                new JavaCodeAnalyzer(NullLogger<JavaCodeAnalyzer>.Instance),
            ],
            graphStore,
            repository,
            manifests,
            new DataSchemaExtractor([new SqlDataArtifactAdapter(), new OrmDataArtifactAdapter()]),
            glossary,
            lifecycle,
            NullLogger<ProjectIndexService>.Instance,
            Options.Create(optimization));

        var runs = new List<IndexBenchmarkRun>();
        GraphSnapshotV2? baselineSnapshot = null;
        var snapshotDifferences = new List<string>();
        var projectId = $"benchmark-nopcommerce-{Guid.NewGuid():N}";
        await repository.SaveAsync(new ProjectEntity
        {
            Id = projectId,
            Name = "nopCommerce benchmark",
            RootPath = root,
        });
        for (var sequence = 0; sequence < warmups + measured; sequence++)
        {
            var warmup = sequence < warmups;
            var clock = Stopwatch.StartNew();
            IndexBenchmarkRun run;
            try
            {
                await service.IndexProjectAsync(projectId);
                clock.Stop();
                var telemetry = service.GetLastRun(projectId)
                    ?? throw new InvalidOperationException("Index completed without run telemetry.");
                if (service.GetActiveSnapshotForDiagnostics(projectId) is { } snapshot)
                {
                    if (baselineSnapshot is null)
                    {
                        baselineSnapshot = snapshot;
                    }
                    else
                    {
                        var comparison = GraphSnapshotComparer.Compare(baselineSnapshot, snapshot);
                        snapshotDifferences.AddRange(comparison.Differences
                            .Take(Math.Max(0, 20 - snapshotDifferences.Count))
                            .Select(difference =>
                                $"snapshotDiff=0->{sequence} {difference.EntityType}:{difference.Identity}:{difference.Kind} expected={difference.Expected} actual={difference.Actual}"));
                    }
                }
                run = new IndexBenchmarkRun(
                    sequence,
                    warmup,
                    clock.ElapsedMilliseconds,
                    telemetry.StageDurationsMilliseconds,
                    telemetry.NodeCount,
                    telemetry.EdgeCount,
                    telemetry.AnalysisSnapshotHash,
                    telemetry.Mode,
                    telemetry.Status,
                    telemetry.Error);
            }
            catch (Exception ex)
            {
                clock.Stop();
                var telemetry = service.GetLastRun(projectId);
                run = new IndexBenchmarkRun(
                    sequence,
                    warmup,
                    clock.ElapsedMilliseconds,
                    telemetry?.StageDurationsMilliseconds ?? new Dictionary<string, long>(),
                    telemetry?.NodeCount ?? 0,
                    telemetry?.EdgeCount ?? 0,
                    telemetry?.AnalysisSnapshotHash,
                    telemetry?.Mode ?? "full",
                    "failed",
                    ex.Message);
            }
            runs.Add(run);
        }

        optimization.ForceFullIndex = false;
        optimization.EnableNoOpFastPath = true;
        var currentBeforeNoOp = await manifests.GetCurrentAsync(projectId);
        output.WriteLine(
            $"beforeNoOp status={currentBeforeNoOp?.Status} requiresRetry={currentBeforeNoOp?.RequiresRetry} " +
            $"files={currentBeforeNoOp?.Files.Count} error={currentBeforeNoOp?.Error}");
        if (service.GetLastDataScanReport(projectId) is { } dataReport)
            output.WriteLine(
                $"dataDiagnostics total={dataReport.Diagnostics.Count} " +
                $"errors={dataReport.Diagnostics.Count(item => item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))} " +
                $"warnings={dataReport.Diagnostics.Count(item => item.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase))}");
        var noOpClock = Stopwatch.StartNew();
        IndexRunTelemetry? noOp = null;
        Exception? noOpFailure = null;
        var cleanupFailures = new List<string>();
        try
        {
            await service.IndexProjectAsync(projectId);
            noOpClock.Stop();
            noOp = service.GetLastRun(projectId);
        }
        catch (Exception ex)
        {
            noOpClock.Stop();
            noOpFailure = ex;
        }
        finally
        {
            try { await graphStore.DeleteProjectAsync(projectId); }
            catch (Exception ex) { cleanupFailures.Add($"Neo4j cleanup failed: {ex.Message}"); }
            try { await manifests.DeleteProjectAsync(projectId); }
            catch (Exception ex) { cleanupFailures.Add($"manifest cleanup failed: {ex.Message}"); }
            try { await repository.DeleteAsync(projectId); }
            catch (Exception ex) { cleanupFailures.Add($"project cleanup failed: {ex.Message}"); }
        }
        output.WriteLine($"noOpElapsedMilliseconds={noOpClock.ElapsedMilliseconds}");
        if (cleanupFailures.Count > 0 && runs.Count > 0)
        {
            var last = runs[^1];
            runs[^1] = last with
            {
                Status = "failed",
                Error = string.Join("; ", cleanupFailures.Prepend(last.Error)
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
            };
        }

        Assert.Null(noOpFailure);
        Assert.NotNull(noOp);
        Assert.Equal("no-op", noOp.Mode);
        Assert.Equal("ready", noOp.Status);
        Assert.Equal(expectedNodes ?? runs[0].NodeCount, noOp.NodeCount);
        Assert.Equal(expectedEdges ?? runs[0].EdgeCount, noOp.EdgeCount);
        Assert.Equal(expectedHash ?? runs[0].AnalysisSnapshotHash, noOp.AnalysisSnapshotHash);
        Assert.True(
            noOpClock.ElapsedMilliseconds <= 2_000,
            $"nopCommerce no-op index took {noOpClock.ElapsedMilliseconds}ms; limit is 2000ms.");

        var environment = await CaptureEnvironmentAsync(root, neo4j);
        var configuration = new IndexBenchmarkConfiguration(
            warmups,
            measured,
            p95Limit,
            expectedNodes,
            expectedEdges,
            expectedHash);
        var report = IndexBenchmarkReportBuilder.Build(fixture, environment, configuration, runs);
        var json = IndexBenchmarkReportBuilder.Serialize(report);
        var reportPath = Environment.GetEnvironmentVariable("WINGMAN_BENCHMARK_REPORT_PATH");
        if (string.IsNullOrWhiteSpace(reportPath))
            reportPath = Path.Combine(Path.GetTempPath(), $"wingman-nopcommerce-benchmark-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
        reportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, json, Encoding.UTF8);
        output.WriteLine(json);
        output.WriteLine($"reportPath={reportPath}");

        foreach (var difference in snapshotDifferences)
            output.WriteLine(difference);

        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Failures));
    }

    private static async Task<IndexBenchmarkEnvironment> CaptureEnvironmentAsync(string root, Neo4jOptions neo4j)
    {
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        string? neo4jVersion = null;
        await using var driver = GraphDatabase.Driver(
            neo4j.Uri,
            AuthTokens.Basic(neo4j.Username, neo4j.Password));
        await using (var session = driver.AsyncSession(options => options.WithDatabase(neo4j.Database)))
        {
            var cursor = await session.RunAsync("CALL dbms.components() YIELD versions RETURN versions[0] AS version LIMIT 1");
            var record = await cursor.SingleAsync();
            neo4jVersion = record["version"].As<string?>();
        }

        return new IndexBenchmarkEnvironment(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            drive.DriveFormat,
            drive.DriveType.ToString(),
            neo4j.Uri,
            neo4j.Database,
            neo4jVersion);
    }

    private static (string Fingerprint, int FileCount) ComputeFixtureFingerprint(string root)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".java", ".sql", ".xml", ".csproj", ".sln", ".slnx", ".props", ".targets",
            ".json", ".yaml", ".yml", ".properties", ".gradle", ".kts",
        };
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", ".vs", "bin", "obj", "node_modules", "target", "dist", "build", "out", "packages" };
        var files = EnumerateFixtureFiles(root, excluded)
            .Where(path => supported.Contains(Path.GetExtension(path)))
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .ToList();

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var contentHash = SHA256.HashData(stream);
            var header = Encoding.UTF8.GetBytes($"{file.Relative}\0{stream.Length}\0{Convert.ToHexString(contentHash).ToLowerInvariant()}\n");
            aggregate.AppendData(header);
        }
        return (Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(), files.Count);
    }

    private static IEnumerable<string> EnumerateFixtureFiles(string root, IReadOnlySet<string> excluded)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> files;
            IEnumerable<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(directory).ToArray();
                subdirectories = Directory.EnumerateDirectories(directory).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
            foreach (var child in subdirectories)
                if (!excluded.Contains(Path.GetFileName(child)))
                    pending.Push(child);
        }
    }

    private static void ValidateNopCommerceFixture(string root)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"{RootVariable} does not exist: {root}");
        var hasSolution = File.Exists(Path.Combine(root, "NopCommerce.sln")) ||
                          File.Exists(Path.Combine(root, "src", "NopCommerce.sln")) ||
                          Directory.EnumerateFiles(root, "NopCommerce.sln", SearchOption.AllDirectories).Any();
        if (!hasSolution)
            throw new InvalidOperationException($"{RootVariable} is not a nopCommerce checkout: NopCommerce.sln was not found.");
    }

    private static Neo4jOptions Neo4jConfiguration() => new()
    {
        Uri = RequiredEnvironment(UriVariable),
        Username = Environment.GetEnvironmentVariable("WINGMAN_BENCHMARK_NEO4J_USERNAME") ?? "neo4j",
        Password = Environment.GetEnvironmentVariable("WINGMAN_BENCHMARK_NEO4J_PASSWORD") ?? "test-only-neo4j-password",
        Database = Environment.GetEnvironmentVariable("WINGMAN_BENCHMARK_NEO4J_DATABASE") ?? "neo4j",
        ConnectionTimeoutSeconds = 10,
        TransactionRetrySeconds = 60,
    };

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable {name} is missing.");

    private static int BoundedInteger(string name, int fallback, int minimum, int maximum)
    {
        var value = OptionalInteger(name) ?? fallback;
        if (value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}; actual value was {value}.");
        return value;
    }

    private static int? OptionalInteger(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException($"{name} must be an integer; actual value was '{raw}'.");
    }

    private sealed class BenchmarkDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class BenchmarkHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("The external benchmark must never download or start Neo4j.");
    }
}

internal sealed class NopCommerceBenchmarkFactAttribute : FactAttribute
{
    public NopCommerceBenchmarkFactAttribute()
    {
        var root = Environment.GetEnvironmentVariable("WINGMAN_BENCHMARK_NOPCOMMERCE_ROOT");
        var uri = Environment.GetEnvironmentVariable("WINGMAN_BENCHMARK_NEO4J_URI");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(uri))
        {
            Skip = "External benchmark not configured. Set both WINGMAN_BENCHMARK_NOPCOMMERCE_ROOT " +
                   "and WINGMAN_BENCHMARK_NEO4J_URI; this test never downloads a fixture or starts Neo4j.";
        }
    }
}
