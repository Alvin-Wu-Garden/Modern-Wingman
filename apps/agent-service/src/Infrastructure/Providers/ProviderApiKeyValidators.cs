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
/// 由後端驗證 BYOK 憑證，避免 WebView 直接連線 AI Provider。
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
            using var request = ProviderModelsRequestFactory.Create(profile, apiKey, baseUrl);
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
                "Provider API Key 驗證失敗。ProfileId={ProfileId}, ExceptionType={ErrorType}",
                profile.Id,
                ex.GetType().Name);
            return ApiKeyValidationResult.Invalid($"無法連線至 {profile.DisplayName} 驗證 API Key。");
        }
    }

}

/// <summary>
/// 建立供憑證驗證與模型探索共用的已驗證請求。
/// </summary>
public static class ProviderModelsRequestFactory
{
    public static HttpRequestMessage Create(
        ModelProviderProfile profile,
        string apiKey,
        string? candidateBaseUrl)
    {
        var protocol = profile.EffectiveProtocol;
        var baseUrl = ResolveBaseUrl(profile, candidateBaseUrl);

        return protocol switch
        {
            ProviderProtocol.Anthropic => CreateAnthropicRequest(baseUrl, apiKey),
            ProviderProtocol.AzureOpenAI => CreateAzureRequest(baseUrl, profile.AzureApiVersion, apiKey),
            ProviderProtocol.OpenAI or ProviderProtocol.OpenAICompatible => CreateBearerRequest(baseUrl, apiKey),
            _ => throw new InvalidOperationException(
                $"Provider Profile「{profile.Id}」的類型不受支援。"),
        };
    }

    private static string ResolveBaseUrl(ModelProviderProfile profile, string? candidateBaseUrl)
    {
        var value = candidateBaseUrl ?? profile.BaseUrl;
        if (string.IsNullOrWhiteSpace(value))
        {
            value = profile.EffectiveProtocol is ProviderProtocol.OpenAI or ProviderProtocol.OpenAICompatible
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
