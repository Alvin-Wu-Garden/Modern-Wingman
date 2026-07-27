using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Infrastructure.Persistence;
using AgentService.Infrastructure.Providers;
using AgentService.Modules.GraphRAG;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentService.UnitTests;

/// <summary>
/// 驗證專案資料庫設定的機密邊界與 SQLite 唯讀抽取。
/// 這些測試不連外部資料庫，也不建立 Neo4j 資料。
/// </summary>
public sealed class ProjectDatabaseConfigurationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wingman-project-db-tests",
        Guid.NewGuid().ToString("N"));
    private SqliteConnection _settingsConnection = null!;
    private IDbContextFactory<AppDbContext> _factory = null!;

    /// <summary>建立每個測試獨立的 in-memory 設定資料庫。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _settingsConnection = new SqliteConnection("DataSource=:memory:");
        await _settingsConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_settingsConnection)
            .Options;
        _factory = new Factory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.Projects.AddRange(
            CreateProject("project-db-test"),
            CreateProject("sqlite-project"));
        await db.SaveChangesAsync();
    }

    /// <summary>清除測試檔案，避免把測試 SQLite 留在使用者磁碟。</summary>
    public async Task DisposeAsync()
    {
        await _settingsConnection.DisposeAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SqlPassword_IsEncryptedHiddenAndRetainedWhenUpdateOmitsPassword()
    {
        var store = new ProjectDatabaseConfigurationStore(
            _factory,
            new TestSecretProtector());
        var original = SqlPasswordConfiguration(password: "db-secret");

        await store.SaveAsync(original);

        var publicValue = await store.GetAsync(original.ProjectId);
        Assert.NotNull(publicValue);
        Assert.Null(publicValue.Password);
        Assert.True(publicValue.HasPassword);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = Assert.Single(db.ProjectDatabaseConfigurations);
            Assert.NotEqual("db-secret", row.ProtectedPassword);
            Assert.Equal("test-protected", row.EncryptionScheme);
        }

        await store.SaveAsync(original with
        {
            DatabaseName = "Renamed",
            Password = null,
        });
        var internalValue = await store.GetAsync(original.ProjectId, includePassword: true);
        Assert.Equal("db-secret", internalValue!.Password);
        Assert.Equal("Renamed", internalValue.DatabaseName);
    }

    [Fact]
    public async Task SwitchingAwayFromSqlPassword_RemovesStoredSecret()
    {
        var store = new ProjectDatabaseConfigurationStore(
            _factory,
            new TestSecretProtector());
        var original = SqlPasswordConfiguration(password: "db-secret");
        await store.SaveAsync(original);

        await store.SaveAsync(original with
        {
            Authentication = SqlServerAuthentication.IntegratedSecurity,
            Username = null,
            Password = null,
            HasPassword = false,
        });

        await using var db = await _factory.CreateDbContextAsync();
        var row = Assert.Single(db.ProjectDatabaseConfigurations);
        Assert.Null(row.ProtectedPassword);
        Assert.Null(row.EncryptionScheme);
    }

    [Fact]
    public async Task SqliteSource_IsReadOnlyAndExtractsOnlySchemaDependencies()
    {
        var databasePath = Path.Combine(_root, "sample.db");
        await CreateSampleDatabaseAsync(databasePath);
        var source = await CreateSqliteSourceAsync(databasePath);
        var builder = new SqliteConnectionStringBuilder(source.ConnectionString);
        Assert.Equal(SqliteOpenMode.ReadOnly, builder.Mode);

        var sqlServer = new SqlServerGraphExtractor(
            NullLogger<SqlServerGraphExtractor>.Instance);
        var extractor = new ProjectGraphDatabaseExtractor(
            sqlServer,
            NullLogger<ProjectGraphDatabaseExtractor>.Instance);
        var fragment = await extractor.ExtractAsync(source);

        Assert.Equal(2, fragment.Nodes.Count);
        var edge = Assert.Single(fragment.Edges);
        Assert.Equal(GraphEdgeKind.Reads, edge.Kind);
        Assert.Contains(
            fragment.Nodes,
            node => node.Name == "active_items" && node.Role == GraphRoles.View);
        Assert.Contains(
            fragment.Nodes,
            node => node.Name == "items" && node.Role == GraphRoles.Table);

        await using var readOnlyConnection = new SqliteConnection(source.ConnectionString);
        await readOnlyConnection.OpenAsync();
        await using var write = readOnlyConnection.CreateCommand();
        write.CommandText = "INSERT INTO items(name) VALUES ('blocked')";
        await Assert.ThrowsAnyAsync<SqliteException>(() => write.ExecuteNonQueryAsync());
    }

    [Fact]
    public void Dpapi_RoundTripsWithoutPersistingPlaintext()
    {
        var protector = new DpapiSecretProtector();
        var protectedSecret = protector.Protect("local-secret");

        Assert.Equal("dpapi-current-user-v1", protectedSecret.Scheme);
        Assert.DoesNotContain("local-secret", protectedSecret.Value, StringComparison.Ordinal);
        Assert.Equal(
            "local-secret",
            protector.Unprotect(protectedSecret.Value, protectedSecret.Scheme));
    }

    [Fact]
    public async Task ProviderApiKey_IsEncryptedAndOrderingCreatesOnlyRequestedRows()
    {
        var store = new ProviderSettingStore(
            _factory,
            Options.Create(new AgentServiceOptions()),
            new TestSecretProtector());

        await store.SetValidatedCredentialAsync(
            "openai-byok",
            "provider-secret",
            "https://api.example.test/v1");
        Assert.Equal("provider-secret", store.GetApiKey("openai-byok"));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = Assert.Single(db.ProviderSettings);
            Assert.NotEqual("provider-secret", row.ProtectedApiKey);
            Assert.Equal("test-protected", row.EncryptionScheme);
        }

        await store.ReorderAsync([
            ("openai-byok", 1),
            ("copilot-default", 0),
        ]);
        var settings = await store.GetAllAsync();
        Assert.Equal(["copilot-default", "openai-byok"], settings.Select(item => item.ProfileId));

        await store.RemoveApiKeyAsync("openai-byok");
        Assert.Null(store.GetApiKey("openai-byok"));
    }

    [Fact]
    public async Task TestEndpoint_ConnectsToCandidateSqliteWithoutSavingConfiguration()
    {
        var hostDatabasePath = Path.Combine(_root, "endpoint-host.db");
        var candidateDatabasePath = Path.Combine(_root, "candidate.db");
        await CreateSampleDatabaseAsync(candidateDatabasePath);
        await using var factory = new DatabaseEndpointFactory(hostDatabasePath);
        using var client = factory.CreateClient();
        var projectId = await CreateEndpointProjectAsync(client);

        using var testResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/database/test",
            new
            {
                provider = "Sqlite",
                sqlitePath = candidateDatabasePath,
            });
        Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);
        using (var document = JsonDocument.Parse(
                   await testResponse.Content.ReadAsStreamAsync()))
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());

        // 測試成功後仍回傳 204，證明候選路徑沒有被當成正式設定保存。
        using var getResponse = await client.GetAsync(
            $"/api/projects/{projectId}/database");
        Assert.Equal(HttpStatusCode.NoContent, getResponse.StatusCode);
    }

    [Fact]
    public async Task TestEndpoint_DoesNotPersistCandidateSqlPasswordWhenConnectionFails()
    {
        var hostDatabasePath = Path.Combine(_root, "endpoint-secret-host.db");
        await using var factory = new DatabaseEndpointFactory(hostDatabasePath);
        using var client = factory.CreateClient();
        var projectId = await CreateEndpointProjectAsync(client);

        using var testResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/database/test",
            new
            {
                provider = "SqlServer",
                server = "127.0.0.1",
                port = 1,
                databaseName = "TransientCandidate",
                authentication = "SqlPassword",
                username = "candidate-user",
                password = "candidate-password",
                trustServerCertificate = true,
            });
        Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);
        using (var document = JsonDocument.Parse(
                   await testResponse.Content.ReadAsStreamAsync()))
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());

        // 即使候選密碼真的被拿去嘗試連線，也不得產生設定列或 DPAPI 密文。
        using var getResponse = await client.GetAsync(
            $"/api/projects/{projectId}/database");
        Assert.Equal(HttpStatusCode.NoContent, getResponse.StatusCode);
    }

    [Fact]
    public async Task DatabaseListEndpoint_RejectsIncompleteCandidateWithoutSavingConfiguration()
    {
        var hostDatabasePath = Path.Combine(_root, "endpoint-catalog-host.db");
        await using var factory = new DatabaseEndpointFactory(hostDatabasePath);
        using var client = factory.CreateClient();
        var projectId = await CreateEndpointProjectAsync(client);

        // 不完整的候選帳密應在真正連線前被拒絕，且不可因此產生設定資料列。
        using var listResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/database/databases",
            new
            {
                provider = "SqlServer",
                server = "127.0.0.1",
                port = 1433,
                authentication = "SqlPassword",
                username = "",
                password = "candidate-password",
                trustServerCertificate = true,
            });
        Assert.Equal(HttpStatusCode.BadRequest, listResponse.StatusCode);

        using var getResponse = await client.GetAsync(
            $"/api/projects/{projectId}/database");
        Assert.Equal(HttpStatusCode.NoContent, getResponse.StatusCode);
    }

    /// <summary>建立 SQL Auth 測試設定，集中固定不重要的欄位。</summary>
    private static ProjectDatabaseConfiguration SqlPasswordConfiguration(string password) => new(
        "project-db-test",
        ProjectDatabaseProvider.SqlServer,
        "127.0.0.1",
        1433,
        "WingmanTest",
        SqlServerAuthentication.SqlPassword,
        "tester",
        password,
        true,
        true,
        null,
        DateTimeOffset.UtcNow);

    /// <summary>建立符合外鍵需求的最小專案資料列。</summary>
    private ProjectRecord CreateProject(string projectId) => new()
    {
        Id = projectId,
        Name = projectId,
        RootPath = _root,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>經由正式 provider 建立 SQLite 唯讀來源，而非在測試中自行拼連線字串。</summary>
    private async Task<GraphDatabaseSource> CreateSqliteSourceAsync(string databasePath)
    {
        var store = new ProjectDatabaseConfigurationStore(
            _factory,
            new TestSecretProtector());
        await store.SaveAsync(new ProjectDatabaseConfiguration(
            "sqlite-project",
            ProjectDatabaseProvider.Sqlite,
            null,
            null,
            "sample",
            null,
            null,
            null,
            false,
            false,
            databasePath,
            DateTimeOffset.UtcNow));
        var provider = new ProjectGraphDatabaseSourceProvider(store);
        return (await provider.GetAsync(new AgentService.Domain.Models.ProjectEntity
        {
            Id = "sqlite-project",
            Name = "SQLite test",
            RootPath = _root,
        }))!;
    }

    /// <summary>建立一張含資料的 table 與一個 view，證明索引只讀 schema、不複製資料列。</summary>
    private static async Task CreateSampleDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO items(name) VALUES ('secret business row');
            CREATE VIEW active_items AS SELECT id, name FROM items;
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>透過正式 API 建立端點測試專案並回傳後端產生的 ProjectId。</summary>
    private async Task<string> CreateEndpointProjectAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/projects",
            new { name = "Database endpoint test", rootPath = _root });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// 以獨立 SQLite 啟動完整 REST host，避免端點測試碰觸使用者的 wingman_dev.db。
    /// </summary>
    private sealed class DatabaseEndpointFactory(string databasePath)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // 端點測試停用 SQLite 連線池，確保 WebApplicationFactory 關閉後
            // 不會殘留檔案鎖，暫存資料庫可在同一測試生命週期內確實刪除。
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ConnectionString;
            builder.UseEnvironment("Production");
            builder.UseSetting(
                "ConnectionStrings:WingmanDb",
                connectionString);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:WingmanDb"] = connectionString,
                    ["Neo4jLifecycle:Mode"] = "disabled",
                }));
        }
    }

    /// <summary>可檢查密文行為、但不依賴 Windows 使用者狀態的測試 protector。</summary>
    private sealed class TestSecretProtector : ISecretProtector
    {
        public ProtectedSecret Protect(string plaintext) => new(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)),
            "test-protected");

        public string Unprotect(string value, string scheme) =>
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}
