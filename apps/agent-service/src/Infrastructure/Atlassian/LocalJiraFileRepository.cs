using System.Text.Json;
using System.Text.Json.Serialization;
using AgentService.Application.Atlassian;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Atlassian;

/// <summary>
/// 從本機目錄讀取測試用 JIRA 議題檔案（<see cref="NormalizedJiraIssue"/> 序列化 JSON）。
///
/// 使用方式：
///   1. 將任一 NormalizedJiraIssue 序列化後存成 {KEY}.json，放入設定的 Directory。
///   2. 在 appsettings.Development.json 加入 "LocalJiraFiles": { "Enabled": true }。
///   3. 前端呼叫 GET /api/atlassian/jira/local-files 取得可選清單。
///   4. 分析時帶入 localFileKey 即可略過 JIRA 連線。
///
/// LocalJiraFiles.Enabled = false 時，所有方法均回傳空結果，不影響正式流程。
/// </summary>
public sealed class LocalJiraFileRepository
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly LocalJiraFileOptions _options;
    private readonly ILogger<LocalJiraFileRepository> _logger;

    public LocalJiraFileRepository(
        IOptions<LocalJiraFileOptions> options,
        ILogger<LocalJiraFileRepository> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    // ── 公開 API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 回傳目錄下所有可用檔案的摘要清單。
    /// 未啟用或目錄不存在時回傳空清單。
    /// </summary>
    public IReadOnlyList<LocalJiraFileSummary> ListFiles()
    {
        if (!_options.Enabled)
        {
            return [];
        }

        var dir = ResolveDirectory();
        _logger.LogDebug("ListFiles 解析目錄。CWD={Cwd}, ResolvedDir={Dir}, Exists={Exists}",
            Directory.GetCurrentDirectory(), dir, Directory.Exists(dir));

        if (!Directory.Exists(dir))
        {
            return [];
        }

        var result = new List<LocalJiraFileSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            var summary = TryReadSummary(file);
            result.Add(new LocalJiraFileSummary(key, summary));
        }

        return result;
    }

    /// <summary>
    /// 讀取指定 Key 的本機議題檔案。
    /// 未啟用、檔案不存在或解析失敗時回傳 null。
    /// </summary>
    public NormalizedJiraIssue? Load(string fileKey)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(fileKey))
        {
            _logger.LogDebug("LocalJiraFiles 未啟用或 fileKey 為空，略過本機檔案讀取。Enabled={Enabled}, FileKey={FileKey}",
                _options.Enabled, fileKey);
            return null;
        }

        var dir = ResolveDirectory();
        var path = Path.Combine(dir, $"{fileKey}.json");

        _logger.LogDebug("嘗試讀取本機 JIRA 檔案。CWD={Cwd}, ResolvedDir={Dir}, FilePath={Path}",
            Directory.GetCurrentDirectory(), dir, path);

        if (!File.Exists(path))
        {
            _logger.LogDebug("本機 JIRA 檔案不存在：{Path}", path);
            return null;
        }

        try
        {
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var result = JsonSerializer.Deserialize<NormalizedJiraIssue>(json, DeserializeOptions);
            if (result is null)
                _logger.LogWarning("本機 JIRA 檔案反序列化回傳 null。FilePath={Path}", path);
            else
                _logger.LogInformation("已從本機檔案載入 JIRA 議題。Key={Key}, FilePath={Path}", fileKey, path);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取本機 JIRA 檔案時發生錯誤。FilePath={Path}", path);
            return null;
        }
    }

    /// <summary>
    /// 將 NormalizedJiraIssue 序列化後存入目錄，供後續測試使用。
    /// 目錄不存在時自動建立。
    /// </summary>
    public void Save(NormalizedJiraIssue issue)
    {
        var dir = ResolveDirectory();
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{issue.Preview.Key}.json");
        var json = JsonSerializer.Serialize(issue, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });
        File.WriteAllText(path, json);
    }

    // ── 私有輔助 ──────────────────────────────────────────────────────────────

    private string ResolveDirectory()
    {
        var dir = _options.Directory;
        if (Path.IsPathRooted(dir))
            return dir;

        // 相對路徑：依序嘗試 CWD（dotnet run / VS Code task 的工作目錄）
        // 和組件目錄（publish/單一執行檔模式），取第一個存在的。
        var fromCwd = Path.GetFullPath(dir, Directory.GetCurrentDirectory());
        if (Directory.Exists(fromCwd))
            return fromCwd;

        return Path.GetFullPath(dir, AppContext.BaseDirectory);
    }

    private static string TryReadSummary(string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (doc.RootElement.TryGetProperty("Preview", out var preview)
                && preview.TryGetProperty("Summary", out var summary))
            {
                return summary.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // 無法讀取時只回傳空字串，不阻斷清單顯示
        }

        return string.Empty;
    }
}

/// <summary>本機 JIRA 檔案摘要（供 UI 下拉選單使用）。</summary>
public sealed record LocalJiraFileSummary(string Key, string Summary);
