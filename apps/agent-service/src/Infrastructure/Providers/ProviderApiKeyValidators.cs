using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Providers;

public sealed class CopilotApiKeyValidator(ICopilotCredentialRuntime runtime) : IProviderApiKeyValidator
{
    public bool CanValidate(ModelProviderProfile profile) =>
        profile.Kind == ProviderKind.CopilotDefault;

    public Task<ApiKeyValidationResult> ValidateAsync(
        ModelProviderProfile profile,
        string apiKey,
        string? baseUrl,
        CancellationToken ct = default) => runtime.ValidateAsync(apiKey, ct);
}

/// <summary>
/// Validates BYOK credentials from the backend so the WebView never contacts AI providers directly.
/// </summary>
public sealed class HttpProviderApiKeyValidator(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpProviderApiKeyValidator> logger) : IProviderApiKeyValidator
{
    public bool CanValidate(ModelProviderProfile profile) =>
        profile.Kind == ProviderKind.CopilotByok;

    public async Task<ApiKeyValidationResult> ValidateAsync(
        ModelProviderProfile profile,
        string apiKey,
        string? baseUrl,
        CancellationToken ct = default)
    {
        try
        {
            using var request = BuildRequest(profile, apiKey, baseUrl);
            var client = httpClientFactory.CreateClient("key-validator");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            return response.IsSuccessStatusCode
                ? ApiKeyValidationResult.Valid()
                : ApiKeyValidationResult.Invalid(
                    $"{profile.DisplayName} API Key 驗證失敗（HTTP {(int)response.StatusCode}）。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiKeyValidationResult.Invalid($"{profile.DisplayName} API Key 驗證逾時。");
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
        {
            logger.LogWarning(
                "Provider API Key validation failed: profile={ProfileId}, errorType={ErrorType}",
                profile.Id,
                ex.GetType().Name);
            return ApiKeyValidationResult.Invalid($"無法連線至 {profile.DisplayName} 驗證 API Key。");
        }
    }

    private static HttpRequestMessage BuildRequest(
        ModelProviderProfile profile,
        string apiKey,
        string? candidateBaseUrl)
    {
        var providerType = profile.ProviderType?.Trim().ToLowerInvariant();
        var baseUrl = ResolveBaseUrl(profile, candidateBaseUrl);

        return providerType switch
        {
            "anthropic" => CreateAnthropicRequest(baseUrl, apiKey),
            "azure" => CreateAzureRequest(baseUrl, profile.AzureApiVersion, apiKey),
            "openai" => CreateBearerRequest(baseUrl, apiKey),
            _ => throw new InvalidOperationException(
                $"Provider profile '{profile.Id}' has an unsupported provider type."),
        };
    }

    private static string ResolveBaseUrl(ModelProviderProfile profile, string? candidateBaseUrl)
    {
        var value = candidateBaseUrl ?? profile.BaseUrl;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = string.Equals(profile.ProviderType, "openai", StringComparison.OrdinalIgnoreCase)
                ? "https://api.openai.com/v1"
                : null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UriFormatException("Provider Base URL must be an absolute HTTP or HTTPS URL.");
        }

        return value!.TrimEnd('/');
    }

    private static HttpRequestMessage CreateBearerRequest(string baseUrl, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        request.Headers.Authorization = new("Bearer", apiKey);
        return request;
    }

    private static HttpRequestMessage CreateAnthropicRequest(string baseUrl, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    private static HttpRequestMessage CreateAzureRequest(
        string baseUrl,
        string? apiVersion,
        string apiKey)
    {
        var version = string.IsNullOrWhiteSpace(apiVersion) ? "2024-10-21" : apiVersion;
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/openai/models?api-version={Uri.EscapeDataString(version)}");
        request.Headers.Add("api-key", apiKey);
        return request;
    }
}
