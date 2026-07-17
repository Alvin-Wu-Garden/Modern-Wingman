using AgentService.Application.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;

public sealed class ProjectIndexManifestStoreTests
{
    [Fact]
    public async Task FailedAttempt_DoesNotReplaceLastSuccessfulManifest()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await AgentSchemaMigrator.ApplyAsync(db);
            db.Projects.Add(new ProjectRecord
            {
                Id = "p1", Name = "P1", RootPath = "C:/repo", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var store = new ProjectIndexManifestStore(factory);
        var successful = Manifest("v1", IndexManifestStatus.Fresh);
        await store.PromoteAsync(successful);
        await store.SaveAttemptAsync(Manifest("v2", IndexManifestStatus.Failed));

        Assert.Equal("v1", (await store.GetCurrentAsync("p1"))?.Version);
        Assert.Equal("v2", (await store.GetLatestAttemptAsync("p1"))?.Version);
    }

    [Fact]
    public async Task Promote_AtomicallyMovesCurrentPointer()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await AgentSchemaMigrator.ApplyAsync(db);
            db.Projects.Add(new ProjectRecord
            {
                Id = "p1", Name = "P1", RootPath = "C:/repo", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var store = new ProjectIndexManifestStore(factory);
        await store.PromoteAsync(Manifest("v1", IndexManifestStatus.Fresh));
        await store.PromoteAsync(Manifest("v2", IndexManifestStatus.Fresh));

        Assert.Equal("v2", (await store.GetCurrentAsync("p1"))?.Version);
    }

    [Fact]
    public async Task RequiresRetry_RoundTripsInManifestJson()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await AgentSchemaMigrator.ApplyAsync(db);
            db.Projects.Add(new ProjectRecord
            {
                Id = "p1", Name = "P1", RootPath = "C:/repo", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var store = new ProjectIndexManifestStore(factory);
        await store.PromoteAsync(Manifest("retryable", IndexManifestStatus.Partial) with
        {
            RequiresRetry = true,
            Error = "adapter read failed",
        });

        var current = await store.GetCurrentAsync("p1");
        Assert.True(current?.RequiresRetry);
        Assert.Equal(IndexManifestStatus.Partial, current?.Status);
    }

    [Fact]
    public async Task SaveAttempt_CannotDemoteOrOverwritePublishedManifest()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await AgentSchemaMigrator.ApplyAsync(db);
            db.Projects.Add(new ProjectRecord
            {
                Id = "p1", Name = "P1", RootPath = "C:/repo", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var store = new ProjectIndexManifestStore(factory);
        var published = Manifest("published", IndexManifestStatus.Fresh);
        await store.PromoteAsync(published);
        await store.SaveAttemptAsync(published with
        {
            Status = IndexManifestStatus.Failed,
            Error = "project row save failed after graph publish",
        });

        var current = await store.GetCurrentAsync("p1");
        Assert.Equal("published", current?.Version);
        Assert.Equal(IndexManifestStatus.Fresh, current?.Status);
        Assert.Null(current?.Error);
    }

    [Fact]
    public async Task GetByVersion_DoesNotReturnNewerFailedAttempt()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var store = new ProjectIndexManifestStore(new Factory(options));
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await AgentSchemaMigrator.ApplyAsync(db);
            db.Projects.Add(new ProjectRecord
            {
                Id = "p1", Name = "P1", RootPath = "C:/repo", CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await store.SaveAttemptAsync(Manifest("published-in-neo4j", IndexManifestStatus.Indexing));
        await store.SaveAttemptAsync(Manifest("newer-failed", IndexManifestStatus.Failed) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        });

        Assert.Equal(
            "published-in-neo4j",
            (await store.GetByVersionAsync("p1", "published-in-neo4j"))?.Version);
        Assert.Null(await store.GetByVersionAsync("other-project", "published-in-neo4j"));
    }

    private static ProjectIndexManifest Manifest(string version, IndexManifestStatus status) => new(
        "p1", version, "C:/repo", "abc", "fingerprint", [], [], [], "test",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, status);

    private sealed class Factory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
