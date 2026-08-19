using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Speech;

public sealed class SpeechModelManager(
    SpeechPathResolver paths,
    SpeechSettingsStore settingsStore,
    IHttpClientFactory httpClientFactory,
    IOptions<SpeechToTextOptions> options,
    ILogger<SpeechModelManager> logger) : ISpeechModelManager
{
    private readonly SpeechToTextOptions _options = options.Value;

    public async Task<SpeechStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.ModelsDirectory);
        var settings = await settingsStore.GetAsync(ct);
        return BuildStatus(settings);
    }

    public async Task<SpeechStatusDto> DownloadModelAsync(string? modelId, string? url, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.ModelsDirectory);
        var model = SpeechModelCatalog.Get(modelId);
        var sourceUrl = string.IsNullOrWhiteSpace(url)
            ? model.Sources.First().Url
            : url.Trim();

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("模型下載 URL 必須是有效的 HTTP/HTTPS 位址。");
        }

        var destination = paths.GetModelPath(model);
        var tempPath = $"{destination}.download";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        var client = httpClientFactory.CreateClient("speech-download");
        logger.LogInformation("開始下載語音模型。ModelId={ModelId}, Url={Url}", model.Id, sourceUrl);

        try
        {
            await using (var target = File.Create(tempPath))
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await source.CopyToAsync(target, ct);
            }

            ValidateModelFile(tempPath, model);
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        var settings = await settingsStore.SaveAsync(
            new SpeechRuntimeSettings(await GetCurrentLanguageAsync(ct), model.Id),
            ct);
        return BuildStatus(settings);
    }

    public async Task<SpeechStatusDto> ImportModelAsync(string sourcePath, string? modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidOperationException("請選擇要匯入的 Whisper 模型檔。");

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
            throw new FileNotFoundException("找不到模型檔。", fullSourcePath);

        if (!string.Equals(Path.GetExtension(fullSourcePath), ".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只支援匯入 whisper.cpp 的 .bin 模型檔。");

        Directory.CreateDirectory(paths.ModelsDirectory);
        var model = ResolveImportModel(fullSourcePath, modelId);
        var destination = paths.GetModelPath(model);
        var tempPath = $"{destination}.import";

        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            File.Copy(fullSourcePath, tempPath, overwrite: true);
            ValidateModelFile(tempPath, model);
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempPath, destination);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        var settings = await settingsStore.SaveAsync(
            new SpeechRuntimeSettings(await GetCurrentLanguageAsync(ct), model.Id),
            ct);
        return BuildStatus(settings);
    }

    public async Task<SpeechStatusDto> SaveSettingsAsync(SpeechSettingsRequest request, CancellationToken ct = default)
    {
        var current = await settingsStore.GetAsync(ct);
        var next = new SpeechRuntimeSettings(
            request.Language ?? current.Language,
            request.ActiveModelId ?? current.ActiveModelId);
        var saved = await settingsStore.SaveAsync(next, ct);
        return BuildStatus(saved);
    }

    private async Task<string> GetCurrentLanguageAsync(CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(ct);
        return settings.Language;
    }

    private SpeechStatusDto BuildStatus(SpeechRuntimeSettings settings)
    {
        Directory.CreateDirectory(paths.ModelsDirectory);

        var enginePath = paths.FindEnginePath();
        var modelDtos = SpeechModelCatalog.All.Select(ToDto).ToList();
        var active = modelDtos.FirstOrDefault(model => model.Id == settings.ActiveModelId)
                     ?? modelDtos.First(model => model.Id == SpeechModelCatalog.DefaultModelId);

        var engineAvailable = enginePath is not null;
        var ready = engineAvailable && active.Installed;
        var message = ready
            ? null
            : !engineAvailable
                ? "語音轉文字引擎不可用，請確認 whisper-cli.exe 已隨 AgentService 打包。"
                : "尚未安裝語音模型，下載或匯入模型後對話框會顯示麥克風。";

        return new SpeechStatusDto(
            ready,
            engineAvailable,
            enginePath,
            paths.ModelsDirectory,
            active.Id,
            settings.Language,
            Math.Clamp(_options.MaxRecordingSeconds, 10, 600),
            modelDtos,
            message);
    }

    private SpeechModelDto ToDto(SpeechModelDefinition model)
    {
        var modelPath = paths.GetModelPath(model);
        var installed = File.Exists(modelPath);
        var size = installed ? new FileInfo(modelPath).Length : (long?)null;
        var valid = installed && size >= model.MinimumBytes;

        return new SpeechModelDto(
            model.Id,
            model.DisplayName,
            model.FileName,
            model.Description,
            size,
            valid,
            model.Recommended,
            model.Sources.Select(source => new SpeechModelSourceDto(source.Id, source.DisplayName, source.Url)).ToList());
    }

    private static SpeechModelDefinition ResolveImportModel(string sourcePath, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
            return SpeechModelCatalog.Get(modelId);

        var fileName = Path.GetFileName(sourcePath);
        return SpeechModelCatalog.All.FirstOrDefault(model =>
                   string.Equals(model.FileName, fileName, StringComparison.OrdinalIgnoreCase))
               ?? SpeechModelCatalog.Get(SpeechModelCatalog.DefaultModelId);
    }

    private static void ValidateModelFile(string path, SpeechModelDefinition model)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < model.MinimumBytes)
        {
            throw new InvalidOperationException(
                $"模型檔案大小不合理，請確認檔案是 {model.FileName} 且下載/匯入完整。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 對未下載完成的模型檔採 best effort 清理。
        }
    }
}
