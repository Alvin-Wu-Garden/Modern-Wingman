using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentService.UnitTests;

public sealed class ProviderEndpointPersistenceTests
{
    [Fact]
    public async Task InvalidKey_ReturnsInvalidAndDoesNotWriteCandidateToDatabase()
    {
        var databasePath = CreateDatabasePath();
        await using var factory = new ProviderFactory(databasePath, ApiKeyValidationResult.Invalid("rejected"));
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            "/api/providers/openai-byok/key",
            new { apiKey = "candidate-secret", baseUrl = "https://api.openai.com/v1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("invalid", document.RootElement.GetProperty("status").GetString());
        Assert.Null(await ReadStoredKeyAsync(databasePath, "openai-byok"));
    }

    [Fact]
    public async Task ValidKey_ReturnsValidAndWritesCandidateToDatabase()
    {
        var databasePath = CreateDatabasePath();
        await using var factory = new ProviderFactory(databasePath, ApiKeyValidationResult.Valid());
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            "/api/providers/openai-byok/key",
            new { apiKey = "candidate-secret", baseUrl = "https://api.openai.com/v1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("valid", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("candidate-secret", await ReadStoredKeyAsync(databasePath, "openai-byok"));
    }

    [Fact]
    public async Task InvalidReplacement_PreservesExistingDatabaseCredentialAndBaseUrl()
    {
        var databasePath = CreateDatabasePath();
        await using var factory = new ProviderFactory(databasePath, ApiKeyValidationResult.Invalid("rejected"));
        using var client = factory.CreateClient();

        // Start the host so migrations create the real SQLite schema before seeding an existing credential.
        using var initializeResponse = await client.GetAsync("/api/providers");
        initializeResponse.EnsureSuccessStatusCode();
        await WriteStoredCredentialAsync(
            databasePath,
            "openai-byok",
            "existing-secret",
            "https://old.example.test/v1");

        using var response = await client.PutAsJsonAsync(
            "/api/providers/openai-byok/key",
            new { apiKey = "invalid-replacement", baseUrl = "https://new.example.test/v1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("invalid", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            ("existing-secret", "https://old.example.test/v1"),
            await ReadStoredCredentialAsync(databasePath, "openai-byok"));
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ModernWingmanTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "wingman.db");
    }

    private static async Task<string?> ReadStoredKeyAsync(string databasePath, string profileId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ApiKey FROM ProviderSettings WHERE ProfileId = $profileId";
        command.Parameters.AddWithValue("$profileId", profileId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<(string? ApiKey, string? BaseUrl)> ReadStoredCredentialAsync(
        string databasePath,
        string profileId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ApiKey, BaseUrl FROM ProviderSettings WHERE ProfileId = $profileId";
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task WriteStoredCredentialAsync(
        string databasePath,
        string profileId,
        string apiKey,
        string baseUrl)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProviderSettings (ProfileId, ApiKey, BaseUrl, SortOrder, UpdatedAt)
            VALUES ($profileId, $apiKey, $baseUrl, 0, $updatedAt)
            ON CONFLICT(ProfileId) DO UPDATE SET
                ApiKey = excluded.ApiKey,
                BaseUrl = excluded.BaseUrl,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$apiKey", apiKey);
        command.Parameters.AddWithValue("$baseUrl", baseUrl);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ProviderFactory(
        string databasePath,
        ApiKeyValidationResult validation) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:WingmanDb", $"Data Source={databasePath}");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:WingmanDb"] = $"Data Source={databasePath}",
                    ["Neo4j:Enabled"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProviderApiKeyValidator>();
                services.AddSingleton<IProviderApiKeyValidator>(new StubValidator(validation));
            });
        }
    }

    private sealed class StubValidator(ApiKeyValidationResult result) : IProviderApiKeyValidator
    {
        public bool CanValidate(ModelProviderProfile profile) => true;

        public Task<ApiKeyValidationResult> ValidateAsync(
            ModelProviderProfile profile,
            string apiKey,
            string? baseUrl,
            CancellationToken ct = default) => Task.FromResult(result);
    }
}
