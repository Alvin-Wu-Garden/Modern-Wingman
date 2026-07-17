using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// Provider-agnostic AI observability recorder.
/// Records metadata only by default; never records API keys.
/// </summary>
public interface ILlmTelemetryRecorder
{
    Task<LlmTelemetryRequestHandle?> StartRequestAsync(
        LlmTelemetryRequestStart request,
        CancellationToken ct = default);

    Task MarkFirstTokenAsync(
        LlmTelemetryRequestHandle? handle,
        DateTimeOffset firstTokenAt,
        CancellationToken ct = default);

    Task CompleteRequestAsync(
        LlmTelemetryRequestHandle? handle,
        LlmTelemetryCompletion completion,
        CancellationToken ct = default);

    Task FailRequestAsync(
        LlmTelemetryRequestHandle? handle,
        LlmTelemetryFailure failure,
        CancellationToken ct = default);

    Task<LlmTelemetryRequestHandle?> RetryAsync(LlmTelemetryRequestHandle? handle,ModelProviderProfile profile,string? modelId,string reason,CancellationToken ct=default);
}
