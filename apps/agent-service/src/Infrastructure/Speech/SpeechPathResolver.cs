using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Speech;

public sealed class SpeechPathResolver(IOptions<SpeechToTextOptions> options)
{
    private readonly SpeechToTextOptions _options = options.Value;

    public string ModelsDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_options.ModelsDirectory))
                return Environment.ExpandEnvironmentVariables(_options.ModelsDirectory);

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".Wingman", "models");
        }
    }

    public string SettingsPath
    {
        get
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, ".Wingman", "speech-settings.json");
        }
    }

    public string? FindEnginePath()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.EnginePath))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(_options.EnginePath));
        }

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "tools", "whisper", "win32-x64", "whisper-cli.exe"));
        candidates.Add(Path.Combine(baseDir, "whisper", "win32-x64", "whisper-cli.exe"));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "tools", "whisper", "win32-x64", "whisper-cli.exe")));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "tools", "whisper", "win32-x64", "whisper-cli.exe"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "apps", "agent-service", "tools", "whisper", "win32-x64", "whisper-cli.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public string GetModelPath(SpeechModelDefinition model) =>
        Path.Combine(ModelsDirectory, model.FileName);
}
