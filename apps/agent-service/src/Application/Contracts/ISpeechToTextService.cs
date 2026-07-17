using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface ISpeechModelManager
{
    Task<SpeechStatusDto> GetStatusAsync(CancellationToken ct = default);
    Task<SpeechStatusDto> DownloadModelAsync(string? modelId, string? url, CancellationToken ct = default);
    Task<SpeechStatusDto> ImportModelAsync(string sourcePath, string? modelId, CancellationToken ct = default);
    Task<SpeechStatusDto> SaveSettingsAsync(SpeechSettingsRequest request, CancellationToken ct = default);
}

public interface ISpeechToTextService
{
    Task<SpeechTranscriptionResult> TranscribeAsync(
        Stream audio,
        string contentType,
        CancellationToken ct = default);
}
