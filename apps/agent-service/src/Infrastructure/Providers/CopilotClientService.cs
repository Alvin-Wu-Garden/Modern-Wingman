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
    private Task? _startupTask;
    private volatile bool _ready;

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
        // 若使用者已存入 PAT，優先以 PAT 認證，不需本機 Copilot CLI 登入狀態
        var pat = _settingStore.GetApiKey("copilot-default");
        _startupTask = StartInBackgroundAsync(pat, _lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task StartInBackgroundAsync(string? githubToken, CancellationToken ct)
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
            _logger.LogError(ex, "Copilot CLI 啟動失敗；REST 與其他 Provider 仍可使用，請更新認證後重試。");
        }
    }

    private async Task StartClientInternalAsync(string? githubToken, CancellationToken ct = default)
    {
        _logger.LogInformation("正在啟動 Copilot CLI 進程 (認證模式: {Mode})...",
            githubToken is not null ? "PAT" : "系統登入");

        // Connection = null → SDK 自動解析 bundled binary
        var clientOptions = new CopilotClientOptions
        {
            LogLevel = CopilotLogLevel.Info,
        };

        if (githubToken is not null)
        {
            // GitHubToken 透過環境變數傳入 copilot.exe，優先於本機 OAuth
            clientOptions.GitHubToken = githubToken;
            clientOptions.UseLoggedInUser = false;
        }

        _client = new CopilotClient(clientOptions);
        await _client.StartAsync(ct);
        _logger.LogInformation("Copilot CLI 進程已啟動。");
    }

    /// <summary>
    /// 使用者在設定頁更新或移除 PAT 後，即時重啟 Copilot Client，無需重啟 AgentService。
    /// </summary>
    public async Task RestartWithTokenAsync(string? githubToken, CancellationToken ct = default)
    {
        _logger.LogInformation("Copilot Client 重啟中 (認證模式: {Mode})...",
            githubToken is not null ? "新 PAT" : "系統登入");
        _ready = false;
        if (_client is not null)
        {
            try { await _client.StopAsync(); }
            catch { await _client.ForceStopAsync(); }
            await _client.DisposeAsync();
            _client = null;
        }
        await StartClientInternalAsync(githubToken, ct);
        _ready = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();
        if (_startupTask is not null)
        {
            try { await _startupTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        if (_client is null) return;

        _logger.LogInformation("正在停止 Copilot CLI 進程...");
        try
        {
            await _client.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止 Copilot CLI 時發生例外，強制終止。");
            await _client.ForceStopAsync();
        }
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
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        _lifetime.Dispose();
    }
}
