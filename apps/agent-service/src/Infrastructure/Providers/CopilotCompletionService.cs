using System.Diagnostics;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Telemetry;
using AgentService.Infrastructure.Orchestration;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Providers;

/// <summary>
/// ILlmCompletionService 的 Copilot SDK 實作。
/// 走 CopilotClient → MAF AsAIAgent() 的一次性非串流呼叫。
/// </summary>
public sealed class CopilotCompletionService(
    CopilotClientService copilotClientService,
    IModelProviderService providerService,
    ProviderConfigResolver configResolver,
    CopilotPermissionHandlerFactory permissionHandlerFactory,
    ILlmTelemetryRecorder telemetry,
    IOptions<LlmTelemetryOptions> telemetryOptions,
    ILogger<CopilotCompletionService> logger) : ILlmCompletionService
{
    public Task<string> CompleteAsync(string prompt, CancellationToken ct = default) =>
        CompleteAsync(prompt, telemetryContext: null, ct);

    public async Task<string> CompleteAsync(
        string prompt,
        LlmTelemetryContext? telemetryContext,
        CancellationToken ct = default)
    {
        // Preserve historical behavior: generic completions use Copilot default.
        var profile = await providerService.GetProfileAsync("copilot-default", ct);
        var client = copilotClientService.GetClient();
        var agent = client.AsAIAgent(new SessionConfig
        {
            Streaming = true,
            OnPermissionRequest = permissionHandlerFactory.Create(
                AgentService.Domain.Models.AgentMode.Ask,
                workspacePath: null),
        });

        return await RunCompletionWithTelemetryAsync(
            prompt,
            agent,
            profile,
            profile.ModelId,
            telemetryContext ?? new LlmTelemetryContext("llm_completion"),
            ct);
    }

    public Task<string> CompleteAsync(
        string prompt,
        string? providerProfileId,
        string? modelId,
        CancellationToken ct = default) =>
        CompleteAsync(prompt, providerProfileId, modelId, telemetryContext: null, ct);

    public async Task<string> CompleteAsync(
        string prompt,
        string? providerProfileId,
        string? modelId,
        LlmTelemetryContext? telemetryContext,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerProfileId) && string.IsNullOrWhiteSpace(modelId))
            return await CompleteAsync(prompt, telemetryContext, ct);

        var profile = await providerService.GetProfileAsync(providerProfileId, ct);
        var config = await configResolver.BuildSessionConfigAsync(
            profile,
            workspacePath: null,
            conversationHistoryText: null,
            modelOverride: modelId,
            ct: ct);

        var client = copilotClientService.GetClient();
        var agent = client.AsAIAgent(config);

        return await RunCompletionWithTelemetryAsync(
            prompt,
            agent,
            profile,
            modelId ?? profile.ModelId,
            telemetryContext ?? new LlmTelemetryContext("llm_completion"),
            ct);
    }

    private async Task<string> RunCompletionWithTelemetryAsync(
        string prompt,
        AIAgent agent,
        ModelProviderProfile profile,
        string? requestedModelId,
        LlmTelemetryContext telemetryContext,
        CancellationToken ct)
    {
        var timeoutOptions = telemetryOptions.Value;
        var firstTokenTimeout = ToTimeout(timeoutOptions.FirstTokenTimeoutSeconds, 60);
        var idleStreamTimeout = ToTimeout(timeoutOptions.IdleStreamTimeoutSeconds, 120);
        var totalRequestTimeout = ToTimeout(timeoutOptions.TotalRequestTimeoutSeconds, 600);

        var handle = await telemetry.StartRequestAsync(
            new LlmTelemetryRequestStart(
                telemetryContext,
                profile,
                requestedModelId,
                IsStreaming: true,
                Prompt: prompt),
            CancellationToken.None);

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalCts.CancelAfter(totalRequestTimeout);

        var sb = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        var firstTokenSeen = false;
        var tokenEventCount = 0;
        var interTokenTotalMs = 0L;
        DateTimeOffset? lastTokenAt = null;
        UsageDetails? lastUsage = null;

        try
        {
            for(var attemptNo=1;;attemptNo++)
            {
                using var streamCts=CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);streamCts.CancelAfter(firstTokenTimeout);
                try
                {
                    await foreach (var update in agent.RunStreamingAsync(prompt,cancellationToken:streamCts.Token))
                    {
                        if(update.Contents is{} contents)foreach(var item in contents)if(item is UsageContent uc){lastUsage=uc.Details;break;}
                        if(string.IsNullOrEmpty(update.Text))continue;var tokenAt=DateTimeOffset.UtcNow;if(!firstTokenSeen){firstTokenSeen=true;await telemetry.MarkFirstTokenAsync(handle,tokenAt,CancellationToken.None);}else if(lastTokenAt is not null)interTokenTotalMs+=Math.Max(0,(long)(tokenAt-lastTokenAt.Value).TotalMilliseconds);lastTokenAt=tokenAt;tokenEventCount++;streamCts.CancelAfter(idleStreamTimeout);sb.Append(update.Text);
                    }
                    break;
                }
                catch(OperationCanceledException)when(!ct.IsCancellationRequested&&!totalCts.IsCancellationRequested&&!firstTokenSeen&&attemptNo<2)
                {
                    handle=await telemetry.RetryAsync(handle,profile,requestedModelId,LlmTimeoutKind.FirstToken,CancellationToken.None);
                }
            }

            stopwatch.Stop();
            var usage = BuildTokenUsage(lastUsage);
            await telemetry.CompleteRequestAsync(
                handle,
                new LlmTelemetryCompletion(
                    Response: sb.ToString(),
                    Usage: usage,
                    CompletedAt: DateTimeOffset.UtcNow,
                    DurationMs: stopwatch.ElapsedMilliseconds,
                    TimeToLastByteMs: stopwatch.ElapsedMilliseconds,
                    AvgInterTokenMs: tokenEventCount > 1
                        ? interTokenTotalMs / Math.Max(1, tokenEventCount - 1)
                        : null,
                    TokensPerSecond: CalculateTokensPerSecond(usage, stopwatch.Elapsed)),
                CancellationToken.None);

            logger.LogDebug(
                "LLM completion 完成（provider={ProviderId}, model={ModelId}, {Length} 字元）",
                profile.Id,
                requestedModelId ?? profile.ModelId ?? "(default)",
                sb.Length);
            return sb.ToString();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            await telemetry.FailRequestAsync(
                handle,
                new LlmTelemetryFailure(
                    LlmTelemetryStatus.Cancelled,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    TimeoutKind: null,
                    ErrorType: "client_cancelled",
                    ErrorCode: null,
                    HttpStatus: null,
                    ErrorMessage: "Client cancelled the completion request"),
                CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            var timeoutKind = ResolveTimeoutKind(firstTokenSeen, totalRequestTimeout, stopwatch.Elapsed);
            var message = BuildTimeoutMessage(timeoutKind, profile.DisplayName, requestedModelId);
            await telemetry.FailRequestAsync(
                handle,
                new LlmTelemetryFailure(
                    LlmTelemetryStatus.TimedOut,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    timeoutKind,
                    ErrorType: "timeout",
                    ErrorCode: timeoutKind,
                    HttpStatus: null,
                    ErrorMessage: message,
                    RetryReason: timeoutKind),
                CancellationToken.None);
            throw new TimeoutException(message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await telemetry.FailRequestAsync(
                handle,
                new LlmTelemetryFailure(
                    LlmTelemetryStatus.Failed,
                    DateTimeOffset.UtcNow,
                    stopwatch.ElapsedMilliseconds,
                    TimeoutKind: null,
                    ErrorType: "provider_error",
                    ErrorCode: ex.GetType().Name,
                    HttpStatus: null,
                    ErrorMessage: ex.Message),
                CancellationToken.None);
            throw;
        }
    }

    private static TokenUsage? BuildTokenUsage(UsageDetails? usage) =>
        usage is null
            ? null
            : new TokenUsage(
                (int)(usage.InputTokenCount ?? 0),
                (int)(usage.OutputTokenCount ?? 0),
                (int)(usage.TotalTokenCount ?? 0));

    private static TimeSpan ToTimeout(int seconds, int fallbackSeconds) =>
        TimeSpan.FromSeconds(Math.Max(1, seconds > 0 ? seconds : fallbackSeconds));

    private static string ResolveTimeoutKind(
        bool firstTokenSeen,
        TimeSpan totalTimeout,
        TimeSpan elapsed)
    {
        if (elapsed >= totalTimeout - TimeSpan.FromSeconds(1))
            return LlmTimeoutKind.TotalRequest;

        return firstTokenSeen ? LlmTimeoutKind.IdleStream : LlmTimeoutKind.FirstToken;
    }

    private static string BuildTimeoutMessage(
        string timeoutKind,
        string providerName,
        string? modelId) =>
        timeoutKind switch
        {
            LlmTimeoutKind.FirstToken =>
                $"模型回應逾時：{providerName} / {modelId ?? "(default)"} 超過等待上限仍未回傳第一個 token。",
            LlmTimeoutKind.IdleStream =>
                $"模型串流逾時：{providerName} / {modelId ?? "(default)"} 已開始回應，但中途太久沒有新內容。",
            _ =>
                $"模型請求逾時：{providerName} / {modelId ?? "(default)"} 超過總等待上限。",
        };

    private static double? CalculateTokensPerSecond(TokenUsage? usage, TimeSpan elapsed)
    {
        if (usage?.OutputTokens is not > 0 || elapsed.TotalSeconds <= 0)
            return null;
        return Math.Round(usage.OutputTokens / elapsed.TotalSeconds, 2);
    }
}
