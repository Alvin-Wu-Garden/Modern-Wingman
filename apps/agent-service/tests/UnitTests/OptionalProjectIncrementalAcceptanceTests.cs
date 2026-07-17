using System.Diagnostics;
using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;
using AgentService.Infrastructure.CodeAnalysis;
using AgentService.Infrastructure.CodeGraph;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentService.UnitTests;

[Collection("External index acceptance")]
public sealed class OptionalProjectIncrementalAcceptanceTests
{
    [Neo4jFact]
    public async Task BodyDelta_EqualsFullShadow_PreservesCalls_AndMeetsTenSecondGate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wingman-incremental-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var firstProjectId = $"incremental-{Guid.NewGuid():N}";
        var shadowProjectId = $"incremental-shadow-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            var target = Path.Combine(root, "Target.cs");
            var caller = Path.Combine(root, "Caller.cs");
            await File.WriteAllTextAsync(target,
                "namespace Acme; public class Target { public void One() { } public void Two() { } }");
            await File.WriteAllTextAsync(caller,
                "namespace Acme; public class Caller { public void Execute(Target target) { target.One(); } }");
            var additionalCallers = new List<string>();
            for (var index = 1; index < 25; index++)
            {
                var path = Path.Combine(root, $"Caller{index:D2}.cs");
                additionalCallers.Add(path);
                await File.WriteAllTextAsync(path,
                    $"namespace Acme; public class Caller{index:D2} {{ public void Execute(Target target) {{ target.One(); }} }}");
            }

            var neo4j = Options.Create(new Neo4jOptions
            {
                Uri = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_URI")!,
                Username = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_USERNAME") ?? "neo4j",
                Password = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_PASSWORD") ?? "test-only-neo4j-password",
                Database = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_DATABASE") ?? "neo4j",
            });
            await using var graph = new Neo4jCodeGraphStore(neo4j, NullLogger<Neo4jCodeGraphStore>.Instance);
            Assert.True(await graph.PingAsync());

            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new IncrementalDbFactory(dbOptions);
            await using (var db = new AppDbContext(dbOptions))
            {
                await db.Database.EnsureCreatedAsync();
                await AgentSchemaMigrator.ApplyAsync(db);
            }

            var repository = new ProjectRepository(factory);
            var manifests = new ProjectIndexManifestStore(factory);
            var glossary = new DomainGlossarySqliteStore(factory);
            await repository.SaveAsync(Project(firstProjectId, root));
            await repository.SaveAsync(Project(shadowProjectId, root));
            await using var lifecycle = new Neo4jLifecycleService(
                Options.Create(new Neo4jLifecycleOptions { Mode = "external" }),
                neo4j,
                graph,
                new IncrementalHttpClientFactory(),
                NullLogger<Neo4jLifecycleService>.Instance);
            var service = new ProjectIndexService(
                [new RoslynCodeAnalyzer(NullLogger<RoslynCodeAnalyzer>.Instance)],
                graph,
                repository,
                manifests,
                new DataSchemaExtractor([new SqlDataArtifactAdapter(), new OrmDataArtifactAdapter()]),
                glossary,
                lifecycle,
                NullLogger<ProjectIndexService>.Instance,
                Options.Create(new ProjectIndexOptimizationOptions
                {
                    EnableNoOpFastPath = true,
                    EnableBodyOnlyIncremental = true,
                }));

            await service.IndexProjectAsync(firstProjectId);
            await File.WriteAllTextAsync(caller,
                "namespace Acme; public class Caller { public void Execute(Target target) { target.Two(); } }");

            var clock = Stopwatch.StartNew();
            var incremented = await service.IncrementalIndexAsync(firstProjectId);
            clock.Stop();
            Assert.NotNull(incremented);
            Assert.Equal("incremental-body", service.GetLastRun(firstProjectId)?.Mode);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
                $"1-file body delta took {clock.Elapsed.TotalMilliseconds:F0}ms.");

            await service.IndexProjectAsync(shadowProjectId);
            var incrementalManifest = await manifests.GetCurrentAsync(firstProjectId);
            var shadowManifest = await manifests.GetCurrentAsync(shadowProjectId);
            var incrementalSnapshot = service.GetActiveSnapshotForDiagnostics(firstProjectId)!;
            var shadowSnapshot = service.GetActiveSnapshotForDiagnostics(shadowProjectId)!;
            var comparison = GraphSnapshotComparer.Compare(shadowSnapshot, incrementalSnapshot);
            Assert.True(comparison.IsEquivalent,
                string.Join(Environment.NewLine, comparison.Differences.Take(20).Select(difference =>
                    $"{difference.EntityType}:{difference.Identity}:{difference.Kind}\nexpected={difference.Expected}\nactual={difference.Actual}")));
            Assert.Equal(shadowManifest?.AnalysisSnapshotHash, incrementalManifest?.AnalysisSnapshotHash);
            Assert.Equal(shadowManifest?.NodeCount, incrementalManifest?.NodeCount);
            Assert.Equal(shadowManifest?.EdgeCount, incrementalManifest?.EdgeCount);

            var calls = await graph.QueryVisualGraphAsync(firstProjectId,
                """
                MATCH (caller:CodeNode {projectId: $projectId, name: 'Execute'})
                      -[:CALLS]->(target:CodeNode {projectId: $projectId})
                WHERE caller.key STARTS WITH 'Acme.Caller.Execute('
                RETURN target.name AS target
                """);
            Assert.Contains(calls.Rows, row => string.Equals(row["target"]?.ToString(), "Two", StringComparison.Ordinal));
            Assert.DoesNotContain(calls.Rows, row => string.Equals(row["target"]?.ToString(), "One", StringComparison.Ordinal));

            var noChangeClock = Stopwatch.StartNew();
            Assert.Null(await service.IncrementalIndexAsync(firstProjectId));
            noChangeClock.Stop();
            Assert.True(noChangeClock.Elapsed < TimeSpan.FromSeconds(2),
                $"No-change detection took {noChangeClock.Elapsed.TotalMilliseconds:F0}ms.");

            await File.WriteAllTextAsync(caller,
                "namespace Acme; public class Caller { public void Renamed(Target target) { target.Two(); } }");
            await service.IncrementalIndexAsync(firstProjectId);
            Assert.Equal("full", service.GetLastRun(firstProjectId)?.Mode);

            await File.WriteAllTextAsync(caller,
                "namespace Acme; public class Caller { public void Renamed(Target target) { target.One(); } }");
            for (var index = 1; index < 25; index++)
            {
                await File.WriteAllTextAsync(additionalCallers[index - 1],
                    $"namespace Acme; public class Caller{index:D2} {{ public void Execute(Target target) {{ target.Two(); }} }}");
            }
            var mediumClock = Stopwatch.StartNew();
            await service.IncrementalIndexAsync(firstProjectId);
            mediumClock.Stop();
            Assert.Equal("incremental-body", service.GetLastRun(firstProjectId)?.Mode);
            Assert.True(mediumClock.Elapsed < TimeSpan.FromSeconds(30),
                $"25-file body delta took {mediumClock.Elapsed.TotalMilliseconds:F0}ms.");

            await service.IndexProjectAsync(shadowProjectId);
            var mediumIncremental = service.GetActiveSnapshotForDiagnostics(firstProjectId)!;
            var mediumShadow = service.GetActiveSnapshotForDiagnostics(shadowProjectId)!;
            var mediumComparison = GraphSnapshotComparer.Compare(mediumShadow, mediumIncremental);
            Assert.True(mediumComparison.IsEquivalent,
                string.Join(Environment.NewLine, mediumComparison.Differences.Take(20)));
        }
        finally
        {
            try
            {
                var options = Options.Create(new Neo4jOptions
                {
                    Uri = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_URI")!,
                    Username = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_USERNAME") ?? "neo4j",
                    Password = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_PASSWORD") ?? "test-only-neo4j-password",
                    Database = Environment.GetEnvironmentVariable("WINGMAN_TEST_NEO4J_DATABASE") ?? "neo4j",
                });
                await using var cleanup = new Neo4jCodeGraphStore(options, NullLogger<Neo4jCodeGraphStore>.Instance);
                await cleanup.DeleteProjectAsync(firstProjectId);
                await cleanup.DeleteProjectAsync(shadowProjectId);
            }
            catch { /* best-effort cleanup; assertions retain the original failure */ }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static ProjectEntity Project(string id, string root) => new()
    {
        Id = id,
        Name = id,
        RootPath = root,
        IndexStatus = ProjectIndexStatus.NotIndexed,
    };

    private sealed class IncrementalDbFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class IncrementalHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException(
            "External Neo4j acceptance must never download or start a runtime.");
    }
}
