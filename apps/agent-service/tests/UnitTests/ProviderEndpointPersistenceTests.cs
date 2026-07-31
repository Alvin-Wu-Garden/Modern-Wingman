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
    public async Task ModelsWithoutStoredKey_ReturnsEmptyListWithoutCallingProvider()
    {
        var providerCallCount = 0;
        var databasePath = CreateDatabasePath();
        await using var factory = new ProviderFactory(
            databasePath,
            ApiKeyValidationResult.Valid(),
            _ =>
            {
                providerCallCount++;
                return ModelsResponse("should-not-be-returned");
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/providers/openai-byok/models");

        response.EnsureSuccessStatusCode();
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ProviderModelResponse>>() ?? []);
        Assert.Equal(0, providerCallCount);
    }

    [Theory]
    [InlineData(
        "openai-byok",
        "https://api.example.test/v1",
        "https://api.example.test/v1/models",
        "Authorization",
        "Bearer stored-secret")]
    [InlineData(
        "anthropic-byok",
        "https://anthropic.example.test",
        "https://anthropic.example.test/v1/models",
        "x-api-key",
        "stored-secret")]
    [InlineData(
        "azure-openai-byok",
        "https://azure.example.test",
        "https://azure.example.test/openai/models?api-version=2024-10-21",
        "api-key",
        "stored-secret")]
    public async Task ModelsWithStoredKey_UsesProviderEndpointAndReturnsOnlyLiveModels(
        string profileId,
        string baseUrl,
        string expectedUrl,
        string expectedHeader,
        string expectedHeaderValue)
    {
        HttpRequestMessage? captured = null;
        var databasePath = CreateDatabasePath();
        await using var factory = new ProviderFactory(
            databasePath,
            ApiKeyValidationResult.Valid(),
            request =>
            {
                captured = Clone(request);
                return ModelsResponse("account-model");
            });
        using var client = factory.CreateClient();

        using var saveResponse = await client.PutAsJsonAsync(
            $"/api/providers/{profileId}/key",
            new { apiKey = "stored-secret", baseUrl });
        saveResponse.EnsureSuccessStatusCode();

        using var response = await client.GetAsync($"/api/providers/{profileId}/models");

        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync<List<ProviderModelResponse>>();
        var model = Assert.Single(models!);
        Assert.Equal("account-model", model.Id);
        Assert.Equal("other", model.Group);
        Assert.Equal(expectedUrl, captured!.RequestUri!.ToString());
        Assert.Equal(
            expectedHeaderValue,
            expectedHeader == "Authorization"
                ? captured.Headers.Authorization!.ToString()
                : Assert.Single(captured.Headers.GetValues(expectedHeader)));
    }

    [Fact]
    public async Task ProviderModelsFailure_ReturnsEmptyListWithoutFallback()
    {
        var databasePath = CreateDatabasePath();
        await using var factory = new ProviderFactory(
            databasePath,
            ApiKeyValidationResult.Valid(),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = factory.CreateClient();
        using var saveResponse = await client.PutAsJsonAsync(
            "/api/providers/openrouter-byok/key",
            new { apiKey = "stored-secret", baseUrl = "https://openrouter.example.test/api/v1" });
        saveResponse.EnsureSuccessStatusCode();

        using var response = await client.GetAsync("/api/providers/openrouter-byok/models");

        response.EnsureSuccessStatusCode();
        Assert.Empty(await response.Content.ReadFromJsonAsync<List<ProviderModelResponse>>() ?? []);
    }

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
        Assert.Null(await ReadStoredCiphertextAsync(databasePath, "openai-byok"));
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
        Assert.NotEqual(
            "candidate-secret",
            await ReadStoredCiphertextAsync(databasePath, "openai-byok"));
        Assert.Equal(
            "candidate-secret",
            factory.Services.GetRequiredService<IProviderSettingStore>()
                .GetApiKey("openai-byok"));
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
        var settingStore = factory.Services.GetRequiredService<IProviderSettingStore>();
        await settingStore.SetValidatedCredentialAsync(
            "openai-byok",
            "existing-secret",
            "https://old.example.test/v1");

        using var response = await client.PutAsJsonAsync(
            "/api/providers/openai-byok/key",
            new { apiKey = "invalid-replacement", baseUrl = "https://new.example.test/v1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal("invalid", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("existing-secret", settingStore.GetApiKey("openai-byok"));
        Assert.Equal(
            "https://old.example.test/v1",
            (await settingStore.GetAsync("openai-byok"))!.BaseUrl);
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

    /// <summary>只讀取資料庫密文，用來證明候選 Key 沒有以明文落地。</summary>
    private static async Task<string?> ReadStoredCiphertextAsync(
        string databasePath,
        string profileId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ProtectedApiKey
            FROM ProviderSettings
            WHERE ProfileId = $profileId
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        return await command.ExecuteScalarAsync() as string;
    }

    private sealed class ProviderFactory(
        string databasePath,
        ApiKeyValidationResult validation,
        Func<HttpRequestMessage, HttpResponseMessage>? providerResponse = null)
        : WebApplicationFactory<Program>
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
                if (providerResponse is not null)
                {
                    services.AddHttpClient("key-validator")
                        .ConfigurePrimaryHttpMessageHandler(() =>
                            new DelegateHandler(providerResponse));
                }
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

    private static HttpResponseMessage ModelsResponse(string modelId) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                data = new[] { new { id = modelId, name = "Account Model" } },
            }),
        };

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private sealed record ProviderModelResponse(string Id, string DisplayName, string Group);

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
