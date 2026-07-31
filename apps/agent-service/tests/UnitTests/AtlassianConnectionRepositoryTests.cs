using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentService.UnitTests;

/// <summary>
/// 驗證 AtlassianConnectionRepository 的新增、更新、刪除與 PAT 保留邏輯。
/// </summary>
public sealed class AtlassianConnectionRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new InMemoryFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ── 新增與讀取 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAndGet_RoundTripsMetadata()
    {
        var repo = new AtlassianConnectionRepository(_factory, new TestSecretProtector());
        var conn = new AtlassianConnection
        {
            ServiceType = AtlassianServiceType.Jira,
            BaseUrl = "https://jira.example.com",
            AuthType = AtlassianAuthType.Bearer,
            SecretValue = "my-pat",
        };

        await repo.SaveAsync(conn);
        var loaded = await repo.GetAsync(AtlassianServiceType.Jira);

        Assert.NotNull(loaded);
        Assert.Equal("https://jira.example.com", loaded.BaseUrl);
        Assert.Equal(AtlassianAuthType.Bearer, loaded.AuthType);
        Assert.True(loaded.HasSecret);
        Assert.Null(loaded.SecretValue);    // 明文不應回傳
    }

    [Fact]
    public async Task Save_EncryptsSecret_NotStoredAsPlaintext()
    {
        var repo = new AtlassianConnectionRepository(_factory, new TestSecretProtector());
        var conn = new AtlassianConnection
        {
            ServiceType = AtlassianServiceType.Jira,
            BaseUrl = "https://jira.example.com",
            AuthType = AtlassianAuthType.Bearer,
            SecretValue = "super-secret-pat",
        };

        await repo.SaveAsync(conn);

        // 直接查 DB，確認明文不在 ProtectedSecret 中
        await using var db = await _factory.CreateDbContextAsync();
        var record = await db.AtlassianConnections.FirstAsync();
        Assert.NotEqual("super-secret-pat", record.ProtectedSecret);
        Assert.Equal("test-protected", record.EncryptionScheme);
    }

    // ── 保留既有 PAT（SecretValue = null 時不覆寫） ──────────────────────────

    [Fact]
    public async Task Update_WithNullSecretValue_PreservesExistingPat()
    {
        var repo = new AtlassianConnectionRepository(_factory, new TestSecretProtector());
        var conn = new AtlassianConnection
        {
            ServiceType = AtlassianServiceType.Jira,
            BaseUrl = "https://jira.example.com",
            AuthType = AtlassianAuthType.Bearer,
            SecretValue = "original-pat",
        };
        await repo.SaveAsync(conn);

        // 取得 DB 中的加密值
        await using var db1 = await _factory.CreateDbContextAsync();
        var originalEncrypted = (await db1.AtlassianConnections.FirstAsync()).ProtectedSecret;

        // 更新時不傳 SecretValue
        conn.SecretValue = null;
        conn.BaseUrl = "https://jira-updated.example.com";
        await repo.SaveAsync(conn);

        await using var db2 = await _factory.CreateDbContextAsync();
        var record = await db2.AtlassianConnections.FirstAsync();
        Assert.Equal("https://jira-updated.example.com", record.BaseUrl);
        Assert.Equal(originalEncrypted, record.ProtectedSecret);    // PAT 未變
    }

    // ── 驗證狀態更新 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Save_VerifiedStatus_PersistsCorrectly()
    {
        var repo = new AtlassianConnectionRepository(_factory, new TestSecretProtector());
        var conn = new AtlassianConnection
        {
            ServiceType = AtlassianServiceType.Wiki,
            BaseUrl = "https://wiki.example.com",
            AuthType = AtlassianAuthType.Basic,
            Username = "wikiuser",
            SecretValue = "wiki-pat",
            IsVerified = true,
            VerifiedAt = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero),
            VerifiedDisplayName = "Wiki User",
        };

        await repo.SaveAsync(conn);
        var loaded = await repo.GetAsync(AtlassianServiceType.Wiki);

        Assert.NotNull(loaded);
        Assert.True(loaded.IsVerified);
        Assert.Equal("Wiki User", loaded.VerifiedDisplayName);
        Assert.Equal("wikiuser", loaded.Username);
    }

    // ── 刪除 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesRecord()
    {
        var repo = new AtlassianConnectionRepository(_factory, new TestSecretProtector());
        var conn = new AtlassianConnection
        {
            ServiceType = AtlassianServiceType.Jira,
            BaseUrl = "https://jira.example.com",
            AuthType = AtlassianAuthType.Bearer,
            SecretValue = "pat",
        };
        await repo.SaveAsync(conn);

        await repo.DeleteAsync(AtlassianServiceType.Jira);
        Assert.Null(await repo.GetAsync(AtlassianServiceType.Jira));
    }

    // ── 各 ServiceType 互不干擾 ──────────────────────────────────────────────

    [Fact]
    public async Task SaveJiraAndWiki_AreIndependent()
    {
        var repo = new AtlassianConnectionRepository(_factory, new TestSecretProtector());
        await repo.SaveAsync(new AtlassianConnection
        {
            ServiceType = AtlassianServiceType.Jira,
            BaseUrl = "https://jira.example.com",
            AuthType = AtlassianAuthType.Bearer,
            SecretValue = "jira-pat",
        });
        await repo.SaveAsync(new AtlassianConnection
        {
            ServiceType = AtlassianServiceType.Wiki,
            BaseUrl = "https://wiki.example.com",
            AuthType = AtlassianAuthType.Basic,
            Username = "wikiuser",
            SecretValue = "wiki-pat",
        });

        var jira = await repo.GetAsync(AtlassianServiceType.Jira);
        var wiki = await repo.GetAsync(AtlassianServiceType.Wiki);

        Assert.NotNull(jira);
        Assert.NotNull(wiki);
        Assert.Equal("https://jira.example.com", jira.BaseUrl);
        Assert.Equal("https://wiki.example.com", wiki.BaseUrl);
        Assert.Null(wiki.SecretValue);  // 明文不回傳
        Assert.True(wiki.HasSecret);
    }

    // ── 測試輔助 ─────────────────────────────────────────────────────────────

    private sealed class InMemoryFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public ProtectedSecret Protect(string plaintext) =>
            new(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)), "test-protected");

        public string Unprotect(string value, string scheme) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
