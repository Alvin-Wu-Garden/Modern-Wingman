using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Speech;

public sealed class SpeechSettingsStore(
    SpeechPathResolver paths,
    IOptions<SpeechToTextOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SpeechToTextOptions _options = options.Value;

    public async Task<SpeechRuntimeSettings> GetAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(paths.SettingsPath))
            {
                await using var stream = File.OpenRead(paths.SettingsPath);
                var settings = await JsonSerializer.DeserializeAsync<SpeechRuntimeSettings>(stream, JsonOptions, ct);
                if (settings is not null)
                    return Normalize(settings);
            }
        }
        catch
        {
            // 設定檔損壞時回到預設值，避免語音輸入永久無法使用。
        }

        return Normalize(new SpeechRuntimeSettings(_options.DefaultLanguage, _options.DefaultModelId));
    }

    public async Task<SpeechRuntimeSettings> SaveAsync(SpeechRuntimeSettings settings, CancellationToken ct = default)
    {
        var normalized = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        await using var stream = File.Create(paths.SettingsPath);
        await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, ct);
        return normalized;
    }

    private static SpeechRuntimeSettings Normalize(SpeechRuntimeSettings settings)
    {
        var language = settings.Language?.Trim().ToLowerInvariant() switch
        {
            "zh" or "zh-tw" or "zh-cn" or "chinese" => "zh-TW",
            "en" or "english" => "en",
            _ => "auto",
        };

        var model = SpeechModelCatalog.Get(settings.ActiveModelId).Id;
        return new SpeechRuntimeSettings(language, model);
    }
}
