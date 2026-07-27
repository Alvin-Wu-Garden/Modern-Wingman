using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;

public sealed class VcsProfileRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new Factory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveUpdateDelete_RoundTripsCredential()
    {
        var repository = new VcsProfileRepository(_factory, new TestSecretProtector());
        var profile = new VcsConnectionProfile
        {
            Name = "Company Bitbucket",
            VcsType = VcsType.Git,
            ServerType = VcsServerType.BitbucketServer,
            BaseUrl = "https://bitbucket.company.local",
            Username = "developer",
            SecretType = VcsSecretType.AccessToken,
            SecretValue = "secret-token",
            SslVerificationEnabled = false,
        };

        await repository.SaveAsync(profile);
        var loaded = await repository.GetAsync(profile.Id);
        Assert.NotNull(loaded);
        Assert.True(loaded.HasSecret);
        Assert.Equal("secret-token", loaded.SecretValue);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var stored = Assert.Single(db.VcsCredentials);
            Assert.NotEqual("secret-token", stored.SecretValue);
            Assert.Equal("test-protected", stored.EncryptionScheme);
        }
        Assert.False(loaded.SslVerificationEnabled);

        profile.Name = "Updated";
        await repository.SaveAsync(profile);
        Assert.Equal("Updated", (await repository.GetAsync(profile.Id))!.Name);

        await repository.DeleteAsync(profile.Id);
        Assert.Null(await repository.GetAsync(profile.Id));
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class TestSecretProtector : AgentService.Application.Contracts.ISecretProtector
    {
        public AgentService.Application.Contracts.ProtectedSecret Protect(string plaintext) =>
            new(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)), "test-protected");

        public string Unprotect(string value, string scheme) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
