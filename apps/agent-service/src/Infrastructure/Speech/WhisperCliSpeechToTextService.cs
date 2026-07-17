using System.Diagnostics;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Speech;

public sealed class WhisperCliSpeechToTextService(
    SpeechPathResolver paths,
    SpeechSettingsStore settingsStore,
    IOptions<SpeechToTextOptions> options,
    ILogger<WhisperCliSpeechToTextService> logger) : ISpeechToTextService
{
    private readonly SpeechToTextOptions _options = options.Value;

    public async Task<SpeechTranscriptionResult> TranscribeAsync(
        Stream audio,
        string contentType,
        CancellationToken ct = default)
    {
        if (!contentType.Contains("audio", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("語音轉文字只接受音訊內容。");
        }

        var enginePath = paths.FindEnginePath();
        if (enginePath is null)
            throw new InvalidOperationException("語音轉文字引擎不可用。");

        var settings = await settingsStore.GetAsync(ct);
        var model = SpeechModelCatalog.Get(settings.ActiveModelId);
        var modelPath = paths.GetModelPath(model);
        if (!File.Exists(modelPath))
            throw new InvalidOperationException("尚未安裝語音模型。");

        var tempDir = Path.Combine(Path.GetTempPath(), "ModernWingman", "speech");
        Directory.CreateDirectory(tempDir);
        var audioPath = Path.Combine(tempDir, $"{Guid.NewGuid():N}.wav");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using (var file = File.Create(audioPath))
            {
                await audio.CopyToAsync(file, ct);
            }

            var language = ToWhisperLanguage(settings.Language);
            var text = await RunWhisperAsync(enginePath, modelPath, audioPath, language, ct);
            stopwatch.Stop();

            return new SpeechTranscriptionResult(text.Trim(), settings.Language, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            TryDelete(audioPath);
        }
    }

    private async Task<string> RunWhisperAsync(
        string enginePath,
        string modelPath,
        string audioPath,
        string language,
        CancellationToken ct)
    {
        var engineDir = Path.GetDirectoryName(enginePath)!;
        var threads = Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = enginePath,
            WorkingDirectory = engineDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var existingPath = process.StartInfo.Environment.TryGetValue("PATH", out var pathValue)
            ? pathValue
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        process.StartInfo.Environment["PATH"] = $"{engineDir};{existingPath}";

        process.StartInfo.ArgumentList.Add("-m");
        process.StartInfo.ArgumentList.Add(modelPath);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(audioPath);
        process.StartInfo.ArgumentList.Add("-l");
        process.StartInfo.ArgumentList.Add(language);
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add(threads.ToString());
        process.StartInfo.ArgumentList.Add("-ng");
        process.StartInfo.ArgumentList.Add("-nt");
        process.StartInfo.ArgumentList.Add("-np");

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var timeout = TimeSpan.FromSeconds(Math.Max(90, _options.MaxRecordingSeconds * 3));
        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            throw new TimeoutException("語音轉文字逾時，請縮短單次錄音長度後再試。");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            logger.LogWarning(
                "whisper-cli failed with code {ExitCode}: {Error}",
                process.ExitCode,
                stderr);
            throw new InvalidOperationException("語音轉文字失敗，請確認模型檔與音訊格式正確。");
        }

        return stdout;
    }

    private static string ToWhisperLanguage(string language) => language switch
    {
        "zh-TW" => "zh",
        "en" => "en",
        _ => "auto",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary audio cleanup should not mask transcription errors.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
