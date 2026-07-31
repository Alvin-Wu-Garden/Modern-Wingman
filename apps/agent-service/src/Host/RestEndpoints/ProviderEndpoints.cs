using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// Provider / API Key REST 端點。
///
/// GET    /api/providers                      → 列出所有 provider profiles（含 sortOrder）
/// GET    /api/providers/{id}/key-status      → 取得 API Key 狀態
/// GET    /api/providers/{id}/models          → 列出該 provider 可用的模型
/// PUT    /api/providers/{id}/key             → 儲存 API Key
/// DELETE /api/providers/{id}/key             → 移除 API Key
/// PUT    /api/providers/reorder              → 批次更新排序
/// </summary>
public static class ProviderEndpoints
{
    public static IEndpointRouteBuilder MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/providers");

        group.MapGet("/", ListProviders);
        group.MapGet("/{id}/key-status", GetKeyStatus);
        group.MapGet("/{id}/models", ListModels);
        group.MapPut("/{id}/key", SetKey);
        group.MapDelete("/{id}/key", DeleteKey);
        group.MapPut("/reorder", Reorder);
        // Dedicated endpoint for the Skills library's GitHub PAT.
        // AI provider credentials are validated and saved atomically by PUT /{id}/key.
        group.MapPost("/validate-key", ValidateGitHubAccessToken);

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListProviders(
        IModelProviderService providerService,
        IProviderSettingStore settingStore,
        CancellationToken ct)
    {
        var profiles = providerService.ListProfiles();
        var dbSettings = await settingStore.GetAllAsync(ct);
        var sortMap = dbSettings.ToDictionary(x => x.ProfileId, x => x.SortOrder);

        // 未在 DB 有排序記錄的 profile，依 appsettings 順序排在最後
        var result = profiles
            .Select((p, idx) => new
            {
                p.Id,
                p.DisplayName,
                p.Kind,
                p.ModelId,
                p.ProviderType,
                p.BaseUrl,
                SortOrder = sortMap.TryGetValue(p.Id, out var s) ? s : idx + 1000,
            })
            .OrderBy(x => x.SortOrder)
            .ToList();

        return Results.Ok(result);
    }

    private static async Task<IResult> GetKeyStatus(
        string id,
        IModelProviderService providerService,
        IProviderSettingStore settingStore,
        CopilotClientService copilotClientService,
        CancellationToken ct)
    {
        var profile = providerService.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile is null) return Results.NotFound();

        var dbSetting = await settingStore.GetAsync(id, ct);
        var hasEnvVar = settingStore.HasEnvVar(id);
        var hasStoredKey = !string.IsNullOrWhiteSpace(dbSetting?.ProtectedApiKey);

        CopilotRuntimeStatusDto? runtimeStatus = null;
        if (profile.Kind == ProviderKind.CopilotDefault)
        {
            var runtime = copilotClientService.GetRuntimeStatus();
            runtimeStatus = new CopilotRuntimeStatusDto(
                runtime.State,
                runtime.IsAuthenticated,
                runtime.Login,
                runtime.AuthType,
                runtime.CopilotPlan,
                runtime.ModelCount,
                runtime.Error);
        }

        return Results.Ok(new ProviderKeyStatusDto(
            id,
            profile.DisplayName,
            hasEnvVar,
            hasStoredKey,
            StoredBaseUrl: dbSetting?.BaseUrl,
            SortOrder: dbSetting?.SortOrder ?? 0,
            RuntimeStatus: runtimeStatus));
    }

    private static async Task<IResult> ListModels(
        string id,
        IModelProviderService providerService,
        CopilotClientService copilotClientService,
        IProviderSettingStore settingStore,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var profile = providerService.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile is null) return Results.NotFound();

        var apiKey = settingStore.GetApiKey(profile.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Ok(Array.Empty<ProviderModelDto>());

        var models = profile.Kind switch
        {
            ProviderKind.CopilotDefault => await ListCopilotModelsAsync(copilotClientService, ct),
            _ => await ListByokModelsAsync(
                profile,
                apiKey,
                settingStore,
                httpClientFactory,
                ct),
        };

        return Results.Ok(models);
    }

    /// <summary>
    /// 透過 Copilot SDK 列出所有可用的模型，並依 group 分類。
    /// ModelInfo 屬性：Id, Name, Capabilities (Supports/Limits), Policy, Billing, SupportedReasoningEfforts
    /// </summary>
    private static async Task<List<ProviderModelDto>> ListCopilotModelsAsync(
        CopilotClientService copilotClientService,
        CancellationToken ct)
    {
        try
        {
            var client = copilotClientService.GetClient();
            var sdkModels = await client.ListModelsAsync(ct);

            var result = new List<ProviderModelDto>();
            foreach (var m in sdkModels)
            {
                var modelId = m.Id ?? string.Empty;
                if (string.IsNullOrWhiteSpace(modelId)) continue;

                result.Add(new ProviderModelDto(
                    Id: modelId,
                    DisplayName: m.Name ?? modelId,
                    Group: InferModelGroup(modelId)
                ));
            }

            return [.. result.OrderBy(m => m.Group).ThenBy(m => m.Id)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<ProviderModelDto>> ListByokModelsAsync(
        ModelProviderProfile profile,
        string apiKey,
        IProviderSettingStore settingStore,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("key-validator");

        try
        {
            var setting = await settingStore.GetAsync(profile.Id, ct);
            using var req = ProviderModelsRequestFactory.Create(
                profile,
                apiKey,
                setting?.BaseUrl);

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return [];

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<ProviderModelsResponse>(
                stream,
                cancellationToken: ct);

            var models = payload?.Data?
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new ProviderModelDto(
                    Id: m.Id!,
                    DisplayName: string.IsNullOrWhiteSpace(m.Name) ? m.Id! : m.Name!,
                    Group: InferModelGroup(m.Id!)))
                .OrderBy(m => m.Group)
                .ThenBy(m => m.Id)
                .ToList();

            return models ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static string InferModelGroup(string modelId)
    {
        var normalized = modelId.Contains('/')
            ? modelId[(modelId.LastIndexOf('/') + 1)..]
            : modelId;

        if (normalized.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)) return "gpt-5";
        if (normalized.StartsWith("gpt-4", StringComparison.OrdinalIgnoreCase)) return "gpt-4";
        if (normalized.StartsWith("gpt-3", StringComparison.OrdinalIgnoreCase)) return "gpt-3.5";
        if (normalized.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) return "o-series";
        if (normalized.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.Contains("-4")) return "claude-4";
            if (normalized.Contains("-3")) return "claude-3";
            return "claude";
        }
        if (normalized.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("google/", StringComparison.OrdinalIgnoreCase)) return "gemini";
        if (normalized.StartsWith("mistral", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("mistralai/", StringComparison.OrdinalIgnoreCase)) return "mistral";
        if (normalized.StartsWith("llama", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("meta-llama/", StringComparison.OrdinalIgnoreCase)) return "llama";
        if (modelId.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase)) return "openrouter";
        return "other";
    }

    private static string? ExtractHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private static async Task<IResult> SetKey(
        string id,
        SetApiKeyRequest request,
        IModelProviderService providerService,
        IProviderCredentialService credentialService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return Results.BadRequest("ApiKey 不可為空。");

        var profile = providerService.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile is null) return Results.NotFound();

        var result = await credentialService.ValidateAndSaveAsync(
            profile,
            request.ApiKey,
            request.BaseUrl ?? profile.BaseUrl,
            ct);

        if (!result.IsValid)
            return Results.Ok(result);

        return Results.Ok(result);
    }

    private static async Task<IResult> DeleteKey(
        string id,
        IProviderSettingStore settingStore,
        IModelProviderService providerService,
        CopilotClientService copilotClientService,
        CancellationToken ct)
    {
        await settingStore.RemoveApiKeyAsync(id, ct);

        // PAT-only：PAT 移除後停止 runtime，不回退至本機 Copilot / gh 登入。
        var profile = providerService.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile?.Kind == ProviderKind.CopilotDefault)
            await copilotClientService.RestartWithTokenAsync(null, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> Reorder(
        ReorderProvidersRequest request,
        IProviderSettingStore settingStore,
        CancellationToken ct)
    {
        if (request.Order is null || request.Order.Count == 0)
            return Results.BadRequest("Order 不可為空。");

        var order = request.Order
            .Select((id, idx) => (ProfileId: id, SortOrder: idx))
            .ToList();

        await settingStore.ReorderAsync(order, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ValidateGitHubAccessToken(
        ValidateGithubAccessTokenRequest request,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return Results.BadRequest("ApiKey 不可為空。");

        try
        {
            var http = httpClientFactory.CreateClient("key-validator");
            using var message = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            message.Headers.Authorization = new("Bearer", request.ApiKey);
            message.Headers.Add("Accept", "application/vnd.github+json");
            using var response = await http.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
                return Results.Ok(new ValidateGithubAccessTokenResult(false, $"HTTP {(int)response.StatusCode}"));

            var scopes = response.Headers.TryGetValues("x-oauth-scopes", out var values)
                ? string.Join(", ", values)
                : "";
            return Results.Ok(new ValidateGithubAccessTokenResult(true, Scopes: scopes));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Ok(new ValidateGithubAccessTokenResult(false, "驗證 GitHub PAT 時發生連線或服務錯誤。"));
        }
    }

    private sealed record ProviderModelsResponse(
        [property: JsonPropertyName("data")] List<ProviderModelInfo>? Data);

    private sealed record ProviderModelInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);
}
