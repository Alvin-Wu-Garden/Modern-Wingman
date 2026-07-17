namespace AgentService.Application.Models;

public sealed record SpeechModelDto(
    string Id,
    string DisplayName,
    string FileName,
    string Description,
    long? InstalledSizeBytes,
    bool Installed,
    bool Recommended,
    IReadOnlyList<SpeechModelSourceDto> Sources);

public sealed record SpeechModelSourceDto(
    string Id,
    string DisplayName,
    string Url);

public sealed record SpeechStatusDto(
    bool Ready,
    bool EngineAvailable,
    string? EnginePath,
    string ModelsDirectory,
    string ActiveModelId,
    string Language,
    int MaxRecordingSeconds,
    IReadOnlyList<SpeechModelDto> Models,
    string? Message);

public sealed record SpeechSettingsDto(
    string Language,
    string ActiveModelId);

public sealed record SpeechDownloadRequest(
    string? ModelId,
    string? Url);

public sealed record SpeechImportPathRequest(
    string Path,
    string? ModelId);

public sealed record SpeechSettingsRequest(
    string? Language,
    string? ActiveModelId);

public sealed record SpeechTranscriptionResult(
    string Text,
    string Language,
    long DurationMs);
