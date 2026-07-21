using System.Net;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class ProviderApiKeyValidatorTests
{
    [Fact]
    public async Task OpenAiProfile_UsesBackendModelsEndpointAndBearerToken()
    {
        HttpRequestMessage? captured = null;
        var validator = CreateValidator(request =>
        {
            captured = Clone(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var profile = Profile("openai", "https://api.example.test/v1");

        var result = await validator.ValidateAsync(profile, "secret-key", null);

        Assert.True(result.IsValid);
        Assert.Equal("https://api.example.test/v1/models", captured!.RequestUri!.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task AnthropicProfile_UsesAnthropicHeaders()
    {
        HttpRequestMessage? captured = null;
        var validator = CreateValidator(request =>
        {
            captured = Clone(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var profile = Profile("anthropic", "https://api.anthropic.com");

        var result = await validator.ValidateAsync(profile, "secret-key", null);

        Assert.True(result.IsValid);
        Assert.Equal("https://api.anthropic.com/v1/models", captured!.RequestUri!.ToString());
        Assert.Equal("secret-key", Assert.Single(captured.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(captured.Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public async Task AzureProfile_UsesConfiguredApiVersionAndApiKeyHeader()
    {
        HttpRequestMessage? captured = null;
        var validator = CreateValidator(request =>
        {
            captured = Clone(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var profile = new ModelProviderProfile
        {
            Id = "azure-byok",
            DisplayName = "azure",
            Kind = ProviderKind.CopilotByok,
            ProviderType = "azure",
            BaseUrl = "https://resource.openai.azure.com",
            AzureApiVersion = "2025-01-01-preview",
        };

        var result = await validator.ValidateAsync(profile, "secret-key", null);

        Assert.True(result.IsValid);
        Assert.Equal(
            "https://resource.openai.azure.com/openai/models?api-version=2025-01-01-preview",
            captured!.RequestUri!.ToString());
        Assert.Equal("secret-key", Assert.Single(captured.Headers.GetValues("api-key")));
    }

    [Fact]
    public async Task ProviderRejection_ReturnsOnlyInvalidStatus()
    {
        var validator = CreateValidator(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await validator.ValidateAsync(Profile("openai", "https://api.example.test/v1"), "bad", null);

        Assert.False(result.IsValid);
        Assert.Equal("invalid", result.Status);
    }

    private static HttpProviderApiKeyValidator CreateValidator(
        Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(
            new TestHttpClientFactory(new HttpClient(new DelegateHandler(response))),
            NullLogger<HttpProviderApiKeyValidator>.Instance);

    private static ModelProviderProfile Profile(string providerType, string baseUrl) => new()
    {
        Id = $"{providerType}-byok",
        DisplayName = providerType,
        Kind = ProviderKind.CopilotByok,
        ProviderType = providerType,
        BaseUrl = baseUrl,
    };

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
