using AgentService.Application.Contracts;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Providers;

/// <summary>
/// 管理 CopilotClient 單例的 Hosted Service。
/// CopilotClient 會在 DI 啟動時 spawn Copilot CLI 進程，
/// 並在應用程式關閉時優雅停止。
///
/// 設計原則：
/// - 一個 AgentService 進程對應一個 CopilotClient（成本高的資源）
/// - 多 session 透過同一個 Client 建立（CLI 支援多 session）
/// - 工作區切換透過 system prompt 注入，不需重啟 Client
/// </summary>
public sealed class CopilotClientService : IHostedService, IAsyncDisposable
{
    private readonly AgentServiceOptions _options;
    private readonly IProviderSettingStore _settingStore;
    private readonly ILogger<CopilotClientService> _logger;
    private CopilotClient? _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private Task? _startupTask;
    private volatile bool _ready;
    private CopilotRuntimeStatus _status = CopilotRuntimeStatus.NotConfigured();

    public CopilotClientService(
        IOptions<AgentServiceOptions> options,
        IProviderSettingStore settingStore,
        ILogger<CopilotClientService> logger)
    {
        _options = options.Value;
        _settingStore = settingStore;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // PAT-only 模式：沒有 PAT 時不啟動 runtime，也不讀取本機 Copilot / gh 登入狀態。
        var pat = _settingStore.GetApiKey("copilot-default");
        if (string.IsNullOrWhiteSpace(pat))
        {
            SetStatus(CopilotRuntimeStatus.NotConfigured());
            return Task.CompletedTask;
        }

        _startupTask = StartInBackgroundAsync(pat, _lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task StartInBackgroundAsync(string githubToken, CancellationToken ct)
    {
        try
        {
            await StartClientInternalAsync(githubToken, ct);
            _ready = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _ready = false;
            SetStatus(CopilotRuntimeStatus.Invalid(SanitizeError(ex.Message, githubToken)));
            await StopClientInternalAsync();
            _logger.LogError("Bundled Copilot runtime 啟動失敗：{Error}",
                SanitizeError(ex.Message, githubToken));
        }
    }

    private async Task StartClientInternalAsync(string githubToken, CancellationToken ct = default)
    {
        SetStatus(CopilotRuntimeStatus.Validating());
        _logger.LogInformation("正在啟動 bundled Copilot runtime (認證模式: PAT)...");

        // Connection = null → SDK 使用建置時複製到 runtimes/*/native 的 bundled binary。
        // UseLoggedInUser = false 避免讀取本機 OAuth、gh auth 與環境變數憑證。
        _client = new CopilotClient(CreatePatOnlyOptions(githubToken));
        await _client.StartAsync(ct);
        var status = await BuildStatusAsync(_client, githubToken, ct);
        SetStatus(status);
        if (!status.IsAuthenticated)
            throw new InvalidOperationException(status.Error ?? "GitHub PAT 無法使用 Copilot Requests。");

        _logger.LogInformation("Bundled Copilot runtime 已完成 PAT 驗證 (帳號: {Login})。", status.Login);
    }

    /// <summary>
    /// 使用者在設定頁更新或移除 PAT 後，即時重啟 Copilot Client，無需重啟 AgentService。
    /// </summary>
    public async Task RestartWithTokenAsync(string? githubToken, CancellationToken ct = default)
    {
        await _clientGate.WaitAsync(ct);
        try
        {
            _logger.LogInformation("Copilot Client 重啟中 (認證模式: {Mode})...",
                string.IsNullOrWhiteSpace(githubToken) ? "未設定" : "PAT");
            await StopClientInternalAsync();

            if (string.IsNullOrWhiteSpace(githubToken))
            {
                SetStatus(CopilotRuntimeStatus.NotConfigured());
                return;
            }

            await StartClientInternalAsync(githubToken, ct);
            _ready = true;
        }
        catch (Exception ex)
        {
            _ready = false;
            SetStatus(CopilotRuntimeStatus.Invalid(SanitizeError(ex.Message, githubToken)));
            throw;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    /// <summary>在不影響現有 Client 的情況下，驗證使用者剛輸入的 PAT。</summary>
    public async Task<CopilotRuntimeStatus> ValidatePatAsync(string githubToken, CancellationToken ct = default)
    {
        var formatError = GitHubPatValidator.GetFormatError(githubToken);
        if (formatError is not null) return CopilotRuntimeStatus.Invalid(formatError);

        CopilotClient? validationClient = null;
        try
        {
            validationClient = new CopilotClient(CreatePatOnlyOptions(githubToken));
            await validationClient.StartAsync(ct);
            return await BuildStatusAsync(validationClient, githubToken, ct);
        }
        catch (Exception ex)
        {
            return CopilotRuntimeStatus.Invalid(SanitizeError(ex.Message, githubToken));
        }
        finally
        {
            if (validationClient is not null)
            {
                try { await validationClient.StopAsync(); }
                catch { await validationClient.ForceStopAsync(); }
                await validationClient.DisposeAsync();
            }
        }
    }

    public CopilotRuntimeStatus GetRuntimeStatus() => Volatile.Read(ref _status);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();
        if (_startupTask is not null)
        {
            try { await _startupTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        await _clientGate.WaitAsync(cancellationToken);
        try { await StopClientInternalAsync(); }
        finally { _clientGate.Release(); }
    }

    /// <summary>取得已啟動的 CopilotClient 實例。</summary>
    public CopilotClient GetClient()
    {
        return _ready && _client is not null ? _client : throw new InvalidOperationException(
            "CopilotClient 尚未就緒；請檢查 GitHub Copilot 認證，或先使用其他 AI 供應商。");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _clientGate.WaitAsync();
        try { await StopClientInternalAsync(); }
        finally { _clientGate.Release(); }
        _clientGate.Dispose();
        _lifetime.Dispose();
    }

    internal static CopilotClientOptions CreatePatOnlyOptions(string githubToken) => new()
    {
        LogLevel = CopilotLogLevel.Info,
        GitHubToken = githubToken,
        UseLoggedInUser = false,
    };

    private static async Task<CopilotRuntimeStatus> BuildStatusAsync(
        CopilotClient client,
        string githubToken,
        CancellationToken ct)
    {
        var auth = await client.GetAuthStatusAsync(ct);
        if (!auth.IsAuthenticated)
            return CopilotRuntimeStatus.Invalid(SanitizeError(
                auth.StatusMessage ?? "PAT 未通過 Copilot 驗證。", githubToken));

        int? modelCount = null;
        try { modelCount = (await client.ListModelsAsync(ct)).Count; }
        catch { /* 帳號已驗證時，模型清單失敗不應掩蓋認證成功。 */ }

        return CopilotRuntimeStatus.Configured(
            auth.Login,
            auth.AuthType?.ToString(),
            null,
            modelCount);
    }

    private async Task StopClientInternalAsync()
    {
        _ready = false;
        if (_client is null) return;

        _logger.LogInformation("正在停止 bundled Copilot runtime...");
        try { await _client.StopAsync(); }
        catch
        {
            // 不記錄 SDK 原始例外，避免其訊息意外帶入認證資訊。
            _logger.LogWarning("停止 bundled Copilot runtime 時發生例外，強制終止。");
            await _client.ForceStopAsync();
        }
        await _client.DisposeAsync();
        _client = null;
    }

    private void SetStatus(CopilotRuntimeStatus status) => Volatile.Write(ref _status, status);

    internal static string SanitizeError(string? error, string? token)
    {
        var safe = string.IsNullOrWhiteSpace(error) ? "Copilot PAT 驗證失敗。" : error;
        return !string.IsNullOrWhiteSpace(token) ? safe.Replace(token, "[REDACTED]", StringComparison.Ordinal) : safe;
    }
}

public sealed record CopilotRuntimeStatus(
    string State,
    bool IsAuthenticated,
    string? Login = null,
    string? AuthType = null,
    string? CopilotPlan = null,
    int? ModelCount = null,
    string? Error = null)
{
    public static CopilotRuntimeStatus NotConfigured() => new("not_configured", false);
    public static CopilotRuntimeStatus Validating() => new("validating", false);
    public static CopilotRuntimeStatus Configured(string? login, string? authType, string? copilotPlan, int? modelCount) =>
        new("configured", true, login, authType, copilotPlan, modelCount);
    public static CopilotRuntimeStatus Invalid(string error) => new("invalid", false, Error: error);
}
