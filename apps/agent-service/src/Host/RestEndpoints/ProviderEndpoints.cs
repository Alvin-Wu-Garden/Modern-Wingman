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
/// PUT    /api/providers/{id}/base-url        → 儲存 BaseUrl（custom-byok 使用）
/// PUT    /api/providers/reorder              → 批次更新排序
/// POST   /api/providers/validate-key         → 後端代理驗證 API Key（繞過 WebView2 SSL 限制）
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
        group.MapPut("/{id}/base-url", SetBaseUrl);
        group.MapPut("/reorder", Reorder);
        group.MapPost("/validate-key", ValidateKey);

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

        var hasEnvVar = settingStore.HasEnvVar(id);
        var hasStoredKey = !hasEnvVar && settingStore.GetApiKey(id) is not null;
        var dbSetting = await settingStore.GetAsync(id, ct);

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

        var models = profile.Kind switch
        {
            ProviderKind.CopilotDefault => await ListCopilotModelsAsync(copilotClientService, ct),
            _ when IsOpenRouterProfile(profile) =>
                await ListOpenRouterModelsAsync(profile, settingStore, httpClientFactory, ct),
            _ => GetByokFixedModels(profile),
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

            // 依 Group → Id 排序
            return result.Count > 0
                ? [.. result.OrderBy(m => m.Group).ThenBy(m => m.Id)]
                : GetCopilotFallbackModels();
        }
        catch
        {
            // SDK 呼叫失敗時，退回已知常見模型清單
            return GetCopilotFallbackModels();
        }
    }

    /// <summary>SDK 無法取得時的備用 Copilot 模型清單。</summary>
    private static List<ProviderModelDto> GetCopilotFallbackModels() =>
    [
        new("gpt-4.1",            "GPT-4.1",            "gpt-4"),
        new("gpt-4o",             "GPT-4o",             "gpt-4"),
        new("gpt-4o-mini",        "GPT-4o mini",        "gpt-4"),
        new("o3",                 "o3",                 "o-series"),
        new("o4-mini",            "o4-mini",            "o-series"),
        new("claude-sonnet-4",    "Claude Sonnet 4",    "claude"),
        new("claude-sonnet-4-5",  "Claude Sonnet 4.5",  "claude"),
        new("claude-sonnet-4.6",  "Claude Sonnet 4.6",  "claude"),
        new("gemini-2.0-flash",   "Gemini 2.0 Flash",   "gemini"),
        new("gemini-2.5-pro",     "Gemini 2.5 Pro",     "gemini"),
    ];

    private static async Task<List<ProviderModelDto>> ListOpenRouterModelsAsync(
        ModelProviderProfile profile,
        IProviderSettingStore settingStore,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var baseUrl = (profile.BaseUrl ?? "https://openrouter.ai/api/v1").TrimEnd('/');
        var http = httpClientFactory.CreateClient("key-validator");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            var apiKey = settingStore.GetApiKey(profile.Id);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Add("Authorization", $"Bearer {apiKey}");

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return GetOpenRouterFallbackModels();

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<OpenRouterModelsResponse>(
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

            return models is { Count: > 0 } ? models : GetOpenRouterFallbackModels();
        }
        catch
        {
            return GetOpenRouterFallbackModels();
        }
    }

    private static List<ProviderModelDto> GetOpenRouterFallbackModels() =>
    [
        new("openrouter/auto",                     "OpenRouter Auto",       "openrouter"),
        new("openai/gpt-4o",                       "GPT-4o",                "gpt-4"),
        new("openai/gpt-4o-mini",                  "GPT-4o mini",           "gpt-4"),
        new("anthropic/claude-sonnet-4.5",         "Claude Sonnet 4.5",     "claude-4"),
        new("google/gemini-2.5-pro",               "Gemini 2.5 Pro",        "gemini"),
        new("meta-llama/llama-3.3-70b-instruct",   "Llama 3.3 70B Instruct", "llama"),
        new("mistralai/mistral-large",             "Mistral Large",         "mistral"),
    ];

    /// <summary>BYOK provider 的固定模型清單（依 providerType）。</summary>
    private static List<ProviderModelDto> GetByokFixedModels(ModelProviderProfile profile) =>
        profile.ProviderType switch
        {
            "anthropic" =>
            [
                new("claude-opus-4-5",           "Claude Opus 4.5",          "claude-4"),
                new("claude-sonnet-4-5",         "Claude Sonnet 4.5",        "claude-4"),
                new("claude-haiku-4-5",          "Claude Haiku 4.5",         "claude-4"),
                new("claude-3-5-sonnet-20241022", "Claude 3.5 Sonnet",        "claude-3"),
                new("claude-3-5-haiku-20241022",  "Claude 3.5 Haiku",         "claude-3"),
                new("claude-3-opus-20240229",     "Claude 3 Opus",            "claude-3"),
            ],
            "azure" =>
            [
                new("gpt-4.1",     "GPT-4.1",     "gpt-4"),
                new("gpt-4o",      "GPT-4o",      "gpt-4"),
                new("gpt-4o-mini", "GPT-4o mini", "gpt-4"),
                new("gpt-4-turbo", "GPT-4 Turbo", "gpt-4"),
            ],
            _ =>
            [
                new("gpt-5.6",        "GPT-5.6",        "gpt-5"),
                new("gpt-4.1",        "GPT-4.1",       "gpt-4"),
                new("gpt-4o",         "GPT-4o",        "gpt-4"),
                new("gpt-4o-mini",    "GPT-4o mini",   "gpt-4"),
                new("gpt-4-turbo",    "GPT-4 Turbo",   "gpt-4"),
                new("gpt-3.5-turbo",  "GPT-3.5 Turbo", "gpt-3.5"),
            ],
        };

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

    private static bool IsOpenRouterProfile(ModelProviderProfile profile) =>
        profile.Id.Contains("openrouter", StringComparison.OrdinalIgnoreCase) ||
        (profile.BaseUrl?.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string? ExtractHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private static string ProviderAuditDetails(ModelProviderProfile? profile, object? extra = null) =>
        JsonSerializer.Serialize(new
        {
            ProviderProfileId = profile?.Id,
            ProviderDisplayName = profile?.DisplayName,
            Kind = profile?.Kind.ToString(),
            profile?.ProviderType,
            BaseUrlHost = ExtractHost(profile?.BaseUrl),
            Extra = extra,
        });

    private static async Task<IResult> SetKey(
        string id,
        SetApiKeyRequest request,
        IModelProviderService providerService,
        IProviderSettingStore settingStore,
        CopilotClientService copilotClientService,
        IAuditEventRecorder audit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return Results.BadRequest("ApiKey 不可為空。");

        var profile = providerService.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile is null) return Results.NotFound();

        if (profile.Kind == ProviderKind.CopilotDefault)
        {
            var validation = await copilotClientService.ValidatePatAsync(request.ApiKey, ct);
            if (!validation.IsAuthenticated)
                return Results.BadRequest(new { error = validation.Error ?? "GitHub PAT 無法使用 Copilot Requests。" });

            // 先驗證新 PAT，再更新持久化設定，避免無效輸入覆蓋既有可用 PAT。
            var previousApiKey = settingStore.GetApiKey(id);
            await settingStore.SetApiKeyAsync(id, request.ApiKey, ct);

            try
            {
                await copilotClientService.RestartWithTokenAsync(request.ApiKey, ct);
            }
            catch
            {
                if (previousApiKey is null) await settingStore.RemoveApiKeyAsync(id, ct);
                else await settingStore.SetApiKeyAsync(id, previousApiKey, ct);

                try { await copilotClientService.RestartWithTokenAsync(previousApiKey, ct); }
                catch { /* 回復舊 Client 失敗時，由 runtime status 顯示診斷訊息。 */ }

                return Results.BadRequest(new { error = "PAT 已驗證，但啟用 bundled Copilot runtime 時失敗。請重新嘗試。" });
            }

            await audit.RecordAsync(
                new AuditEventWrite(
                    EventType: "provider_api_key_saved",
                    TargetType: "provider",
                    TargetId: id,
                    Action: "update",
                    DetailsJson: ProviderAuditDetails(profile, new { KeyStored = true, Authentication = "fine_grained_pat" })),
                CancellationToken.None);

            return Results.NoContent();
        }

        await settingStore.SetApiKeyAsync(id, request.ApiKey, ct);

        await audit.RecordAsync(
            new AuditEventWrite(
                EventType: "provider_api_key_saved",
                TargetType: "provider",
                TargetId: id,
                Action: "update",
                DetailsJson: ProviderAuditDetails(profile, new { KeyStored = true })),
            CancellationToken.None);

        return Results.NoContent();
    }

    private static async Task<IResult> DeleteKey(
        string id,
        IProviderSettingStore settingStore,
        IModelProviderService providerService,
        CopilotClientService copilotClientService,
        IAuditEventRecorder audit,
        CancellationToken ct)
    {
        await settingStore.RemoveApiKeyAsync(id, ct);

        // PAT-only：PAT 移除後停止 runtime，不回退至本機 Copilot / gh 登入。
        var profile = providerService.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile?.Kind == ProviderKind.CopilotDefault)
            await copilotClientService.RestartWithTokenAsync(null, ct);

        await audit.RecordAsync(
            new AuditEventWrite(
                EventType: "provider_api_key_removed",
                TargetType: "provider",
                TargetId: id,
                Action: "update",
                DetailsJson: ProviderAuditDetails(profile, new { KeyStored = false })),
            CancellationToken.None);

        return Results.NoContent();
    }

    private static async Task<IResult> SetBaseUrl(
        string id,
        SetBaseUrlRequest request,
        IModelProviderService providerService,
        IProviderSettingStore settingStore,
        IAuditEventRecorder audit,
        CancellationToken ct)
    {
        var profile = providerService.ListProfiles().FirstOrDefault(p => p.Id == id);
        if (profile is null) return Results.NotFound();

        var before = await settingStore.GetAsync(id, ct);
        await settingStore.SetBaseUrlAsync(id, request.BaseUrl, ct);
        await audit.RecordAsync(
            new AuditEventWrite(
                EventType: "provider_base_url_changed",
                TargetType: "provider",
                TargetId: id,
                Action: "update",
                DetailsJson: ProviderAuditDetails(profile, new
                {
                    BeforeHost = ExtractHost(before?.BaseUrl ?? profile.BaseUrl),
                    AfterHost = ExtractHost(request.BaseUrl ?? profile.BaseUrl),
                })),
            CancellationToken.None);
        return Results.NoContent();
    }

    private static async Task<IResult> Reorder(
        ReorderProvidersRequest request,
        IProviderSettingStore settingStore,
        IAuditEventRecorder audit,
        CancellationToken ct)
    {
        if (request.Order is null || request.Order.Count == 0)
            return Results.BadRequest("Order 不可為空。");

        var order = request.Order
            .Select((id, idx) => (ProfileId: id, SortOrder: idx))
            .ToList();

        await settingStore.ReorderAsync(order, ct);
        await audit.RecordAsync(
            new AuditEventWrite(
                EventType: "provider_order_changed",
                TargetType: "provider",
                TargetId: "provider-list",
                Action: "update",
                DetailsJson: JsonSerializer.Serialize(new { Order = request.Order })),
            CancellationToken.None);
        return Results.NoContent();
    }

    // ─── API Key 後端代理驗證 ──────────────────────────────────────────────────

    /// <summary>
    /// 後端代理呼叫外部 API 驗證金鑰，避免前端 WebView2 的 SSL/CRL 問題。
    /// </summary>
    private static async Task<IResult> ValidateKey(
        ValidateKeyRequest request,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return Results.BadRequest("ApiKey 不可為空。");

        var http = httpClientFactory.CreateClient("key-validator");

        try
        {
            return (request.ProviderType?.ToLowerInvariant()) switch
            {
                "github" => await ValidateGithubKey(http, request.ApiKey, ct),
                "anthropic" => await ValidateAnthropicKey(http, request.ApiKey, ct),
                "azure" => await ValidateAzureKey(http, request.ApiKey, request.BaseUrl, ct),
                _ => await ValidateOpenAiCompatibleKey(http, request.ApiKey, request.BaseUrl, ct),
            };
        }
        catch
        {
            return Results.Ok(new ValidateKeyResult(Valid: false, Error: "驗證金鑰時發生連線或服務錯誤。"));
        }
    }

    private static async Task<IResult> ValidateGithubKey(
        HttpClient http, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.Add("Authorization", $"token {apiKey}");
        req.Headers.Add("Accept", "application/vnd.github+json");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return Results.Ok(new ValidateKeyResult(false, $"HTTP {(int)res.StatusCode}"));
        var scopes = res.Headers.TryGetValues("x-oauth-scopes", out var vals)
            ? string.Join(", ", vals) : "";
        return Results.Ok(new ValidateKeyResult(true, Scopes: scopes));
    }

    private static async Task<IResult> ValidateAnthropicKey(
        HttpClient http, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        using var res = await http.SendAsync(req, ct);
        return Results.Ok(new ValidateKeyResult(
            res.IsSuccessStatusCode,
            res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}"));
    }

    private static async Task<IResult> ValidateAzureKey(
        HttpClient http, string apiKey, string? baseUrl, CancellationToken ct)
    {
        var b = (baseUrl ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(b))
            return Results.Ok(new ValidateKeyResult(false, "需要 Base URL"));
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{b}/openai/models?api-version=2024-10-21");
        req.Headers.Add("api-key", apiKey);
        using var res = await http.SendAsync(req, ct);
        return Results.Ok(new ValidateKeyResult(
            res.IsSuccessStatusCode,
            res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}"));
    }

    private static async Task<IResult> ValidateOpenAiCompatibleKey(
        HttpClient http, string apiKey, string? baseUrl, CancellationToken ct)
    {
        var b = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{b}/models");
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        using var res = await http.SendAsync(req, ct);
        return Results.Ok(new ValidateKeyResult(
            res.IsSuccessStatusCode,
            res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}"));
    }

    private sealed record OpenRouterModelsResponse(
        [property: JsonPropertyName("data")] List<OpenRouterModelInfo>? Data);

    private sealed record OpenRouterModelInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name);
}
