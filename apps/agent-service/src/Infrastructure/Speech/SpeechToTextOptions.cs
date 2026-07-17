namespace AgentService.Infrastructure.Speech;

public sealed class SpeechToTextOptions
{
    public const string SectionName = "SpeechToText";

    public string? ModelsDirectory { get; set; }
    public string? EnginePath { get; set; }
    public string DefaultModelId { get; set; } = "small-q5_1";
    public string DefaultLanguage { get; set; } = "auto";
    public int MaxRecordingSeconds { get; set; } = 120;
}

public sealed record SpeechModelDefinition(
    string Id,
    string DisplayName,
    string FileName,
    string Description,
    long MinimumBytes,
    bool Recommended,
    IReadOnlyList<SpeechModelSource> Sources);

public sealed record SpeechModelSource(
    string Id,
    string DisplayName,
    string Url);

public sealed record SpeechRuntimeSettings(
    string Language,
    string ActiveModelId);
