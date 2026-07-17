using System.Text.Json;

namespace AgentService.UnitTests;

internal sealed record IndexBenchmarkRun(
    int Sequence,
    bool Warmup,
    long ElapsedMilliseconds,
    IReadOnlyDictionary<string, long> StageDurationsMilliseconds,
    int NodeCount,
    int EdgeCount,
    string? AnalysisSnapshotHash,
    string Mode = "full",
    string Status = "ready",
    string? Error = null);

internal sealed record IndexBenchmarkDistribution(
    int SampleCount,
    long MinimumMilliseconds,
    long MaximumMilliseconds,
    long P50Milliseconds,
    long P95Milliseconds);

internal sealed record IndexBenchmarkFixture(
    string Name,
    string FingerprintAlgorithm,
    string Fingerprint,
    int FingerprintedFileCount);

internal sealed record IndexBenchmarkEnvironment(
    string OperatingSystem,
    string OSArchitecture,
    string ProcessArchitecture,
    string Framework,
    int LogicalProcessorCount,
    bool ServerGc,
    long GcAvailableMemoryBytes,
    string? ProcessorIdentifier,
    string DriveFormat,
    string DriveType,
    string Neo4jUri,
    string Neo4jDatabase,
    string? Neo4jVersion);

internal sealed record IndexBenchmarkConfiguration(
    int WarmupRuns,
    int MeasuredRuns,
    long FullIndexP95LimitMilliseconds,
    int? ExpectedNodeCount,
    int? ExpectedEdgeCount,
    string? ExpectedAnalysisSnapshotHash,
    string PercentileMethod = "nearest-rank");

internal sealed record IndexBenchmarkSummary(
    IndexBenchmarkDistribution Total,
    IReadOnlyDictionary<string, IndexBenchmarkDistribution> Stages,
    int NodeCount,
    int EdgeCount,
    string? AnalysisSnapshotHash);

internal sealed record IndexBenchmarkReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    IndexBenchmarkFixture Fixture,
    IndexBenchmarkEnvironment Environment,
    IndexBenchmarkConfiguration Configuration,
    IReadOnlyList<IndexBenchmarkRun> Runs,
    IndexBenchmarkSummary Summary,
    bool Passed,
    IReadOnlyList<string> Failures);

internal static class IndexBenchmarkReportBuilder
{
    public static IndexBenchmarkReport Build(
        IndexBenchmarkFixture fixture,
        IndexBenchmarkEnvironment environment,
        IndexBenchmarkConfiguration configuration,
        IReadOnlyList<IndexBenchmarkRun> runs,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(runs);

        var measured = runs.Where(run => !run.Warmup).ToList();
        if (measured.Count == 0)
            throw new ArgumentException("At least one measured benchmark run is required.", nameof(runs));

        var total = Distribution(measured.Select(run => run.ElapsedMilliseconds));
        var stages = measured
            .SelectMany(run => run.StageDurationsMilliseconds.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(stage => stage, StringComparer.Ordinal)
            .ToDictionary(
                stage => stage,
                stage => Distribution(measured
                    .Where(run => run.StageDurationsMilliseconds.ContainsKey(stage))
                    .Select(run => run.StageDurationsMilliseconds[stage])),
                StringComparer.Ordinal);

        var first = measured[0];
        var failures = new List<string>();
        if (measured.Count != configuration.MeasuredRuns)
            failures.Add($"Measured run count was {measured.Count}, expected {configuration.MeasuredRuns}.");
        if (runs.Count(run => run.Warmup) != configuration.WarmupRuns)
            failures.Add($"Warmup run count was {runs.Count(run => run.Warmup)}, expected {configuration.WarmupRuns}.");
        if (total.P95Milliseconds > configuration.FullIndexP95LimitMilliseconds)
            failures.Add($"Full index p95 was {total.P95Milliseconds} ms, limit is {configuration.FullIndexP95LimitMilliseconds} ms.");

        foreach (var run in runs)
        {
            if (!string.Equals(run.Status, "ready", StringComparison.Ordinal))
                failures.Add($"Run {run.Sequence} status was '{run.Status}': {run.Error}");
            if (!string.Equals(run.Mode, "full", StringComparison.Ordinal))
                failures.Add($"Run {run.Sequence} mode was '{run.Mode}', expected 'full'.");
            if (run.StageDurationsMilliseconds.Count == 0)
                failures.Add($"Run {run.Sequence} did not report stage durations.");
            if (run.NodeCount != first.NodeCount || run.EdgeCount != first.EdgeCount)
                failures.Add($"Run {run.Sequence} graph count {run.NodeCount}/{run.EdgeCount} differs from {first.NodeCount}/{first.EdgeCount}.");
            if (!string.Equals(run.AnalysisSnapshotHash, first.AnalysisSnapshotHash, StringComparison.Ordinal))
                failures.Add($"Run {run.Sequence} analysis snapshot hash differs from the first measured run.");
        }

        var expectedStageNames = first.StageDurationsMilliseconds.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var run in measured)
        {
            if (!expectedStageNames.SetEquals(run.StageDurationsMilliseconds.Keys))
                failures.Add($"Measured run {run.Sequence} stage set differs from the first measured run.");
        }

        if (first.NodeCount <= 0 || first.EdgeCount <= 0)
            failures.Add($"Measured graph was empty or incomplete: {first.NodeCount} nodes/{first.EdgeCount} edges.");
        if (string.IsNullOrWhiteSpace(first.AnalysisSnapshotHash))
            failures.Add("Measured runs did not produce an analysis snapshot hash.");
        if (configuration.ExpectedNodeCount is { } expectedNodes && first.NodeCount != expectedNodes)
            failures.Add($"Node count was {first.NodeCount}, expected {expectedNodes}.");
        if (configuration.ExpectedEdgeCount is { } expectedEdges && first.EdgeCount != expectedEdges)
            failures.Add($"Edge count was {first.EdgeCount}, expected {expectedEdges}.");
        if (configuration.ExpectedAnalysisSnapshotHash is { Length: > 0 } expectedHash &&
            !string.Equals(first.AnalysisSnapshotHash, expectedHash, StringComparison.Ordinal))
        {
            failures.Add($"Analysis snapshot hash was '{first.AnalysisSnapshotHash}', expected '{expectedHash}'.");
        }

        return new IndexBenchmarkReport(
            "wingman-index-benchmark/v1",
            generatedAt ?? DateTimeOffset.UtcNow,
            fixture,
            environment,
            configuration,
            runs,
            new IndexBenchmarkSummary(total, stages, first.NodeCount, first.EdgeCount, first.AnalysisSnapshotHash),
            failures.Count == 0,
            failures);
    }

    internal static IndexBenchmarkDistribution Distribution(IEnumerable<long> samples)
    {
        var values = samples.OrderBy(value => value).ToArray();
        if (values.Length == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        return new IndexBenchmarkDistribution(
            values.Length,
            values[0],
            values[^1],
            NearestRank(values, 0.50),
            NearestRank(values, 0.95));
    }

    internal static long NearestRank(IReadOnlyList<long> sortedSamples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedSamples);
        if (sortedSamples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(sortedSamples));
        if (percentile is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be in (0, 1].");
        var rank = (int)Math.Ceiling(percentile * sortedSamples.Count);
        return sortedSamples[rank - 1];
    }

    public static string Serialize(IndexBenchmarkReport report) => JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    });
}

public sealed class IndexBenchmarkReportTests
{
    [Fact]
    public void NearestRank_ComputesDeclaredP50AndP95()
    {
        var samples = Enumerable.Range(1, 20).Select(value => value * 10L).ToArray();

        Assert.Equal(100, IndexBenchmarkReportBuilder.NearestRank(samples, 0.50));
        Assert.Equal(190, IndexBenchmarkReportBuilder.NearestRank(samples, 0.95));
        Assert.Equal(200, IndexBenchmarkReportBuilder.NearestRank(samples, 1.00));
    }

    [Fact]
    public void NearestRank_RejectsMissingSamplesAndInvalidPercentiles()
    {
        Assert.Throws<ArgumentException>(() => IndexBenchmarkReportBuilder.NearestRank([], 0.95));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexBenchmarkReportBuilder.NearestRank([1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => IndexBenchmarkReportBuilder.NearestRank([1], 1.01));
    }

    [Fact]
    public void Build_ExcludesWarmupAndAggregatesMeasuredStageDistributions()
    {
        var runs = new[]
        {
            Run(0, warmup: true, elapsed: 999, analyze: 900),
            Run(1, warmup: false, elapsed: 100, analyze: 60),
            Run(2, warmup: false, elapsed: 200, analyze: 120),
        };

        var report = IndexBenchmarkReportBuilder.Build(
            Fixture(), Environment(), new IndexBenchmarkConfiguration(1, 2, 250, 10, 20, "hash"), runs);

        Assert.True(report.Passed);
        Assert.Equal(2, report.Summary.Total.SampleCount);
        Assert.Equal(100, report.Summary.Total.P50Milliseconds);
        Assert.Equal(200, report.Summary.Total.P95Milliseconds);
        Assert.Equal(120, report.Summary.Stages["analyze"].P95Milliseconds);
        Assert.Contains("\"schemaVersion\":\"wingman-index-benchmark/v1\"", IndexBenchmarkReportBuilder.Serialize(report));
    }

    [Fact]
    public void Build_FailsGateForSlowOrSemanticallyDifferentMeasuredRun()
    {
        var runs = new[]
        {
            Run(1, warmup: false, elapsed: 100, analyze: 60),
            Run(2, warmup: false, elapsed: 300, analyze: 200) with
            {
                EdgeCount = 19,
                AnalysisSnapshotHash = "different",
            },
        };

        var report = IndexBenchmarkReportBuilder.Build(
            Fixture(), Environment(), new IndexBenchmarkConfiguration(0, 2, 250, 10, 20, "hash"), runs);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, failure => failure.Contains("p95", StringComparison.Ordinal));
        Assert.Contains(report.Failures, failure => failure.Contains("graph count", StringComparison.Ordinal));
        Assert.Contains(report.Failures, failure => failure.Contains("snapshot hash differs", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DoesNotIgnoreFailedWarmup()
    {
        var runs = new[]
        {
            Run(0, warmup: true, elapsed: 100, analyze: 50) with { Status = "failed", Error = "workspace load failed" },
            Run(1, warmup: false, elapsed: 100, analyze: 50),
        };

        var report = IndexBenchmarkReportBuilder.Build(
            Fixture(), Environment(), new IndexBenchmarkConfiguration(1, 1, 250, 10, 20, "hash"), runs);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, failure => failure.Contains("Run 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RejectsWarmupGraphThatDiffersFromMeasuredGraph()
    {
        var runs = new[]
        {
            Run(0, warmup: true, elapsed: 100, analyze: 50) with { AnalysisSnapshotHash = "warm-drift" },
            Run(1, warmup: false, elapsed: 100, analyze: 50),
        };

        var report = IndexBenchmarkReportBuilder.Build(
            Fixture(), Environment(), new IndexBenchmarkConfiguration(1, 1, 250, 10, 20, "hash"), runs);

        Assert.False(report.Passed);
        Assert.Contains(report.Failures, failure =>
            failure.Contains("Run 0 analysis snapshot hash differs", StringComparison.Ordinal));
    }

    private static IndexBenchmarkRun Run(int sequence, bool warmup, long elapsed, long analyze) =>
        new(sequence, warmup, elapsed, new Dictionary<string, long> { ["analyze"] = analyze }, 10, 20, "hash");

    private static IndexBenchmarkFixture Fixture() => new("nopCommerce", "sha256-path-content-v1", "fixture", 100);

    private static IndexBenchmarkEnvironment Environment() => new(
        "test", "x64", "x64", ".NET", 8, false, 1024, "cpu", "NTFS", "Fixed", "bolt://test", "neo4j", "5");
}
