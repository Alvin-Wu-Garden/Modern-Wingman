using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Telemetry;

public sealed class LlmTelemetryRecorder(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<LlmTelemetryOptions> options,
    ILogger<LlmTelemetryRecorder> logger) : ILlmTelemetryRecorder
{
    public async Task<LlmTelemetryRequestHandle?> RetryAsync(LlmTelemetryRequestHandle? handle,ModelProviderProfile profile,string? modelId,string reason,CancellationToken ct=default)
    {
        if(handle is null)return null;try{await using var db=await dbFactory.CreateDbContextAsync(ct);var previous=await db.AiRequestAttempts.FindAsync([handle.AttemptId],ct);var log=await db.AiRequestLogs.FindAsync([handle.RequestLogId],ct);if(previous is null||log is null)return handle;var now=DateTimeOffset.UtcNow;previous.Status=LlmTelemetryStatus.TimedOut;previous.EndedAt=now;previous.DurationMs=DiffMs(previous.StartedAt,now);previous.TimeoutKind=reason;previous.RetryReason=reason;await UpsertProviderAsync(db,profile,now,ct);var effectiveModel=modelId??profile.ModelId??"default";var resolvedModel=await UpsertModelAsync(db,profile.Id,effectiveModel,profile.DisplayName,true,now,ct);var attemptNo=await db.AiRequestAttempts.Where(x=>x.RequestLogId==log.Id).MaxAsync(x=>x.AttemptNo,ct)+1;var attempt=new AiRequestAttemptRecord{RequestLogId=log.Id,AttemptNo=attemptNo,ProviderProfileId=profile.Id,RequestedModelRecordId=resolvedModel.Id,Status=LlmTelemetryStatus.Running,StartedAt=now,RetryReason=reason,ProviderSnapshotJson=BuildProviderSnapshot(profile),ModelSnapshotJson=BuildModelSnapshot(profile.Id,effectiveModel,resolvedModel.Id)};db.AiRequestAttempts.Add(attempt);await db.SaveChangesAsync(ct);return new(log.Id,attempt.Id,handle.TraceId,handle.StartedAt);}catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning(ex,"AI telemetry retry update failed");return handle;}
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex[] SecretPatterns =
    [
        new(@"sk-or-v1-[A-Za-z0-9_\-]+", RegexOptions.Compiled),
        new(@"sk-[A-Za-z0-9_\-]+", RegexOptions.Compiled),
        new(@"github_pat_[A-Za-z0-9_]+", RegexOptions.Compiled),
        new(@"gh[pousr]_[A-Za-z0-9_]+", RegexOptions.Compiled),
        new(@"(?i)(Bearer\s+)[A-Za-z0-9_\-\.]+", RegexOptions.Compiled),
        new(@"(?i)(api[-_ ]?key\s*[:=]\s*)[^\s,;]+", RegexOptions.Compiled),
    ];

    private readonly LlmTelemetryOptions _options = options.Value;

    public async Task<LlmTelemetryRequestHandle?> StartRequestAsync(
        LlmTelemetryRequestStart request,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var traceId = string.IsNullOrWhiteSpace(request.Context.TraceId)
                ? Guid.NewGuid().ToString("N")
                : request.Context.TraceId!;

            await UpsertProviderAsync(db, request.Profile, now, ct);

            var requestedModelId = ResolveRequestedModelId(request.Profile, request.RequestedModelId);
            var requestedModel = await UpsertModelAsync(
                db,
                request.Profile.Id,
                requestedModelId,
                request.Profile.DisplayName,
                request.IsStreaming,
                now,
                ct);

            var providerSnapshot = BuildProviderSnapshot(request.Profile);
            var modelSnapshot = BuildModelSnapshot(request.Profile.Id, requestedModelId, requestedModel.Id);
            var metadataJson = request.MetadataJson ?? request.Context.MetadataJson;

            var log = new AiRequestLogRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TraceId = traceId,
                ParentRequestId = request.Context.ParentRequestId,
                FeatureArea = request.Context.FeatureArea,
                ConversationId = request.Context.ConversationId,
                MessageId = request.Context.MessageId,
                ProjectId = request.Context.ProjectId,
                RunId = request.Context.RunId,
                ProviderProfileId = request.Profile.Id,
                RequestedModelRecordId = requestedModel.Id,
                IsStreaming = request.IsStreaming,
                Status = LlmTelemetryStatus.Running,
                StartedAt = now,
                CreatedAt = now,
                PromptHash = HashText(request.Prompt),
                PromptPreviewRedacted = BuildPreview(request.Prompt),
                ContentStored = false,
                ProviderSnapshotJson = providerSnapshot,
                ModelSnapshotJson = modelSnapshot,
                MetadataJson = metadataJson,
            };

            var attempt = new AiRequestAttemptRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                RequestLogId = log.Id,
                AttemptNo = 1,
                ProviderProfileId = request.Profile.Id,
                RequestedModelRecordId = requestedModel.Id,
                Status = LlmTelemetryStatus.Running,
                StartedAt = now,
                ProviderSnapshotJson = providerSnapshot,
                ModelSnapshotJson = modelSnapshot,
                MetadataJson = metadataJson,
            };

            db.AiRequestLogs.Add(log);
            db.AiRequestAttempts.Add(attempt);
            await db.SaveChangesAsync(ct);

            return new LlmTelemetryRequestHandle(log.Id, attempt.Id, traceId, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI telemetry start failed; continuing request without telemetry");
            return null;
        }
    }

    public async Task MarkFirstTokenAsync(
        LlmTelemetryRequestHandle? handle,
        DateTimeOffset firstTokenAt,
        CancellationToken ct = default)
    {
        if (handle is null) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var log = await db.AiRequestLogs.FindAsync([handle.RequestLogId], ct);
            var attempt = await db.AiRequestAttempts.FindAsync([handle.AttemptId], ct);
            if (log is null || attempt is null) return;

            if (log.FirstTokenAt is null)
            {
                log.FirstTokenAt = firstTokenAt;
                log.TimeToFirstTokenMs = DiffMs(log.StartedAt, firstTokenAt);
            }

            if (attempt.FirstTokenAt is null)
            {
                attempt.FirstTokenAt = firstTokenAt;
                attempt.TimeToFirstTokenMs = DiffMs(attempt.StartedAt, firstTokenAt);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI telemetry first-token update failed");
        }
    }

    public async Task CompleteRequestAsync(
        LlmTelemetryRequestHandle? handle,
        LlmTelemetryCompletion completion,
        CancellationToken ct = default)
    {
        if (handle is null) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var log = await db.AiRequestLogs.FindAsync([handle.RequestLogId], ct);
            var attempt = await db.AiRequestAttempts.FindAsync([handle.AttemptId], ct);
            if (log is null || attempt is null) return;

            var resolvedModel = string.IsNullOrWhiteSpace(completion.ResolvedModelId)
                ? null
                : await UpsertModelAsync(
                    db,
                    log.ProviderProfileId,
                    completion.ResolvedModelId!,
                    completion.ResolvedModelId!,
                    true,
                    completion.CompletedAt,
                    ct);

            log.Status = LlmTelemetryStatus.Succeeded;
            log.CompletedAt = completion.CompletedAt;
            log.DurationMs = completion.DurationMs ?? DiffMs(log.StartedAt, completion.CompletedAt);
            log.TimeToLastByteMs = completion.TimeToLastByteMs ?? log.DurationMs;
            log.AvgInterTokenMs = completion.AvgInterTokenMs;
            log.TokensPerSecond = completion.TokensPerSecond;
            log.ResponseHash = HashText(completion.Response);
            log.ResponsePreviewRedacted = BuildPreview(completion.Response);
            log.ResolvedModelRecordId = resolvedModel?.Id ?? log.RequestedModelRecordId;
            ApplyUsage(log, completion.Usage);

            attempt.Status = LlmTelemetryStatus.Succeeded;
            attempt.EndedAt = completion.CompletedAt;
            attempt.DurationMs = completion.DurationMs ?? DiffMs(attempt.StartedAt, completion.CompletedAt);
            attempt.ResolvedModelRecordId = log.ResolvedModelRecordId;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI telemetry completion update failed");
        }
    }

    public async Task FailRequestAsync(
        LlmTelemetryRequestHandle? handle,
        LlmTelemetryFailure failure,
        CancellationToken ct = default)
    {
        if (handle is null) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var log = await db.AiRequestLogs.FindAsync([handle.RequestLogId], ct);
            var attempt = await db.AiRequestAttempts.FindAsync([handle.AttemptId], ct);
            if (log is null || attempt is null) return;

            var sanitizedError = Sanitize(failure.ErrorMessage);
            var errorType = failure.ErrorType ?? InferErrorType(failure.Status, failure.TimeoutKind);

            log.Status = failure.Status;
            log.TimeoutKind = failure.TimeoutKind;
            log.CompletedAt = failure.CompletedAt;
            log.DurationMs = failure.DurationMs ?? DiffMs(log.StartedAt, failure.CompletedAt);
            log.TimeToLastByteMs = log.DurationMs;
            log.ErrorType = errorType;
            log.ErrorCode = failure.ErrorCode;
            log.HttpStatus = failure.HttpStatus;
            log.ErrorMessageSanitized = sanitizedError;

            attempt.Status = failure.Status;
            attempt.TimeoutKind = failure.TimeoutKind;
            attempt.EndedAt = failure.CompletedAt;
            attempt.DurationMs = failure.DurationMs ?? DiffMs(attempt.StartedAt, failure.CompletedAt);
            attempt.ErrorType = errorType;
            attempt.ErrorCode = failure.ErrorCode;
            attempt.HttpStatus = failure.HttpStatus;
            attempt.ErrorMessageSanitized = sanitizedError;
            attempt.RetryReason = failure.RetryReason ?? failure.TimeoutKind;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI telemetry failure update failed");
        }
    }

    private async Task UpsertProviderAsync(
        AppDbContext db,
        ModelProviderProfile profile,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var host = ExtractHost(profile.BaseUrl);
        var provider = await db.AiProviderProfiles.FindAsync([profile.Id], ct);
        if (provider is null)
        {
            provider = new AiProviderProfileRecord
            {
                ProfileId = profile.Id,
                CreatedAt = now,
            };
            db.AiProviderProfiles.Add(provider);
        }

        provider.DisplayName = profile.DisplayName;
        provider.Kind = profile.Kind.ToString();
        provider.ProviderType = profile.ProviderType;
        provider.BaseUrlHost = host;
        provider.WireApi = profile.WireApi;
        provider.UpdatedAt = now;
    }

    private static async Task<AiModelRecord> UpsertModelAsync(
        AppDbContext db,
        string providerProfileId,
        string modelId,
        string displayName,
        bool? supportsStreaming,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var model = await db.AiModels.FirstOrDefaultAsync(
            x => x.ProviderProfileId == providerProfileId && x.ModelId == modelId,
            ct);
        if (model is null)
        {
            model = new AiModelRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ProviderProfileId = providerProfileId,
                ModelId = modelId,
                CreatedAt = now,
            };
            db.AiModels.Add(model);
        }

        model.DisplayName = string.IsNullOrWhiteSpace(displayName) ? modelId : displayName;
        model.ModelFamily = InferModelFamily(modelId);
        model.SupportsStreaming = supportsStreaming;
        model.UpdatedAt = now;
        return model;
    }

    private static void ApplyUsage(AiRequestLogRecord log, TokenUsage? usage)
    {
        if (usage is null) return;
        log.InputTokens = usage.InputTokens;
        log.OutputTokens = usage.OutputTokens;
        log.TotalTokens = usage.TotalTokens;
    }

    private string? BuildPreview(string? text)
    {
        if (!_options.StoreContentPreviews || string.IsNullOrWhiteSpace(text))
            return null;

        var sanitized = Sanitize(text) ?? string.Empty;
        var limit = Math.Clamp(_options.ContentPreviewChars, 32, 2000);
        return sanitized.Length <= limit ? sanitized : sanitized[..limit];
    }

    private static string? HashText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var result = value;
        foreach (var pattern in SecretPatterns)
        {
            result = pattern.Replace(result, match =>
                match.Groups.Count > 1 && match.Groups[1].Success
                    ? match.Groups[1].Value + "[redacted]"
                    : "[redacted]");
        }
        return result;
    }

    private static string BuildProviderSnapshot(ModelProviderProfile profile) =>
        JsonSerializer.Serialize(new
        {
            profile.Id,
            profile.DisplayName,
            Kind = profile.Kind.ToString(),
            profile.ProviderType,
            BaseUrlHost = ExtractHost(profile.BaseUrl),
            profile.WireApi,
            profile.AzureApiVersion,
        }, JsonOptions);

    private static string BuildModelSnapshot(
        string providerProfileId,
        string modelId,
        string modelRecordId) =>
        JsonSerializer.Serialize(new
        {
            ProviderProfileId = providerProfileId,
            ModelId = modelId,
            ModelRecordId = modelRecordId,
            ModelFamily = InferModelFamily(modelId),
        }, JsonOptions);

    private static string ResolveRequestedModelId(
        ModelProviderProfile profile,
        string? requestedModelId) =>
        string.IsNullOrWhiteSpace(requestedModelId)
            ? profile.ModelId ?? "(provider-default)"
            : requestedModelId.Trim();

    private static string? ExtractHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
    }

    private static string? InferModelFamily(string modelId)
    {
        var normalized = modelId.Contains('/')
            ? modelId[(modelId.LastIndexOf('/') + 1)..]
            : modelId;
        normalized = normalized.ToLowerInvariant();

        if (normalized.Contains("claude")) return "claude";
        if (normalized.Contains("gemini")) return "gemini";
        if (normalized.Contains("gpt") || normalized.Contains("o1") || normalized.Contains("o3")) return "openai";
        if (normalized.Contains("llama")) return "llama";
        if (normalized.Contains("mistral") || normalized.Contains("mixtral")) return "mistral";
        if (normalized.Contains("deepseek")) return "deepseek";
        if (normalized.Contains("qwen")) return "qwen";
        return null;
    }

    private static long DiffMs(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (long)(end - start).TotalMilliseconds);

    private static string InferErrorType(string status, string? timeoutKind)
    {
        if (status == LlmTelemetryStatus.TimedOut || timeoutKind is not null)
            return "timeout";
        if (status == LlmTelemetryStatus.Cancelled)
            return "client_cancelled";
        return "provider_error";
    }
}
