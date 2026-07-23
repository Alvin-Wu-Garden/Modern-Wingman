using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using AgentService.Application.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>Neo4j 生命週期管理設定。</summary>
public sealed class Neo4jLifecycleOptions
{
    public const string SectionName = "Neo4jLifecycle";

    /// <summary>"managed"（App 自動管理）| "external"（使用者自建，僅連線）。</summary>
    public string Mode { get; set; } = "managed";

    /// <summary>Neo4j Community 下載 URL（Windows zip）。</summary>
    public string DownloadUrl { get; set; } =
        "https://dist.neo4j.org/neo4j-community-5.26.0-windows.zip";

    /// <summary>JRE 下載 URL（Neo4j 5.x 需 Java 17/21）。</summary>
    public string JreDownloadUrl { get; set; } =
        "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse";

    /// <summary>離線安裝包目錄（企業無外網時，將 zip 放此處）。</summary>
    public string? OfflinePackageDir { get; set; }
}

/// <summary>
/// Neo4j Community 自動管理服務（WS3.2，使用者決策：App 內建自動管理）。
///
/// 首次啟用「程式碼解析」時：
///   1. 下載（或從離線目錄取得）Neo4j Community zip + JRE zip
///   2. 解壓至 ~/.wingman/neo4j/
///   3. 設定初始密碼、以子行程啟動
/// 之後每次啟動 Agent Service 時檢查並拉起。
/// 設定 Mode=external 時跳過管理，只驗證連線。
/// </summary>
public sealed class Neo4jLifecycleService(
    IOptions<Neo4jLifecycleOptions> lifecycleOptions,
    IOptions<Neo4jOptions> neo4jOptions,
    ICodeGraphStore graphStore,
    IHttpClientFactory httpClientFactory,
    ILogger<Neo4jLifecycleService> logger) : IAsyncDisposable
{
    private readonly Neo4jLifecycleOptions _options = lifecycleOptions.Value;
    private readonly Neo4jOptions _neo4j = neo4jOptions.Value;
    private Process? _process;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    // FileShare.None gives us a process-crash-safe cross-process lock. A Windows Mutex is
    // thread-affine and therefore cannot be held across await continuations safely.

    private static string WingmanNeo4jRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wingman", "neo4j");

    internal static string GetEffectiveOfflinePackageDir(Neo4jLifecycleOptions options) =>
        string.IsNullOrWhiteSpace(options.OfflinePackageDir)
            ? Path.Combine(WingmanNeo4jRoot, "packages")
            : Environment.ExpandEnvironmentVariables(options.OfflinePackageDir);

    internal static string GetOfflinePackageGuidance(Neo4jLifecycleOptions options)
    {
        var offlineDir = GetEffectiveOfflinePackageDir(options);
        return
            $"請將離線安裝包放入 {offlineDir}；" +
            "檔名需符合 neo4j-community*.zip 與 jre*.zip，" +
            "或設定 Neo4jLifecycle:OfflinePackageDir 指向實際目錄。";
    }

    /// <summary>目前狀態，供前端顯示。</summary>
    public string Status { get; private set; } = "stopped";

    /// <summary>最近一次 lifecycle 失敗的可顯示原因；成功後清除。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 確保 Neo4j 可用：已連線 → 直接回傳；managed 模式 → 安裝/啟動。
    /// 回傳 true 表示可用。
    /// </summary>
    public async Task<bool> EnsureAvailableAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        LastError = null;

        // disabled 模式：完全停用 Neo4j，不嘗試任何連線。
        if (string.Equals(_options.Mode, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            Status = "disabled";
            return false;
        }

        // external 是唯一允許連線到非 Wingman-owned Neo4j 的模式。
        if (_options.Mode == "external")
        {
            if (await graphStore.PingAsync(ct))
            {
                Status = "running";
                return true;
            }
            Status = "unreachable";
            LastError = $"外部 Neo4j 無法連線: {_neo4j.Uri}";
            logger.LogWarning("外部 Neo4j 無法連線: {Uri}", _neo4j.Uri);
            return false;
        }

        if (!string.Equals(_options.Mode, "managed", StringComparison.OrdinalIgnoreCase))
        {
            Status = "invalid-configuration";
            LastError = $"不支援的 Neo4j lifecycle mode: {_options.Mode}";
            logger.LogError("{Error}", LastError);
            return false;
        }

        // managed 模式只能重用由這個 lifecycle instance 啟動的 bundled process。
        // 不能因同一 port 上剛好有 Docker／外部 Neo4j 就把它視為 Wingman runtime。
        if (_process is { HasExited: false })
            return await VerifyManagedProcessAsync(ct);

        await _startLock.WaitAsync(ct);
        FileStream? crossProcessLock = null;
        try
        {
            crossProcessLock = await AcquireCrossProcessLockAsync(ct);
            if (_process is { HasExited: false })
                return await VerifyManagedProcessAsync(ct);

            if (!TryGetManagedEndpoint(out var address, out var port))
                return false;

            if (!IsPortAvailable(address, port))
            {
                Status = "port-conflict";
                LastError =
                    $"Wingman 管理的 Neo4j 連接埠 {address}:{port} 已被其他程序占用；" +
                    "managed 模式已拒絕連線，避免誤用 Docker 或外部 Neo4j。";
                logger.LogError("{Error}", LastError);
                return false;
            }

            Status = "installing";
            var home = await EnsureInstalledAsync(progress, ct);
            if (home is null)
            {
                Status = "install-failed";
                return false;
            }

            Status = "starting";
            progress?.Report("正在啟動 Neo4j...");
            var started = await StartProcessAsync(home, ct);
            Status = started ? "running" : "start-failed";
            if (!started)
                LastError ??= "Wingman 管理的 Neo4j 無法啟動。";
            return started;
        }
        finally
        {
            if (crossProcessLock is not null)
                await crossProcessLock.DisposeAsync();
            _startLock.Release();
        }
    }

    // ── 安裝 ──────────────────────────────────────────────────────────────────

    private async Task<string?> EnsureInstalledAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var root = WingmanNeo4jRoot;
        var homeDir = FindNeo4jHome(root);
        if (homeDir is not null && FindJavaExe(root) is not null)
        {
            // Configuration can change between Desktop releases (for example when
            // Windows reserves the historical default Bolt port). Re-apply it for
            // existing installations rather than only when Neo4j is first unpacked.
            ConfigureNeo4j(homeDir);
            return homeDir;
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(GetEffectiveOfflinePackageDir(_options));

        // Neo4j zip
        if (homeDir is null)
        {
            progress?.Report("正在取得 Neo4j Community...");
            var zipPath = await AcquirePackageAsync("neo4j-community", _options.DownloadUrl, root, ct);
            if (zipPath is null)
                return null;
            progress?.Report("正在解壓 Neo4j...");
            ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
            homeDir = FindNeo4jHome(root);
        }

        // JRE
        if (FindJavaExe(root) is null)
        {
            progress?.Report("正在取得 JRE 21...");
            var jreZip = await AcquirePackageAsync("jre", _options.JreDownloadUrl, root, ct);
            if (jreZip is null)
                return null;
            progress?.Report("正在解壓 JRE...");
            var jreDir = Path.Combine(root, "jre");
            Directory.CreateDirectory(jreDir);
            ZipFile.ExtractToDirectory(jreZip, jreDir, overwriteFiles: true);
        }

        if (homeDir is not null)
        {
            ConfigureNeo4j(homeDir);
        }

        return homeDir;
    }

    /// <summary>取得安裝包：離線目錄優先，否則下載。</summary>
    private async Task<string?> AcquirePackageAsync(
        string namePrefix, string url, string root, CancellationToken ct)
    {
        // 離線包
        var offlineDir = GetEffectiveOfflinePackageDir(_options);
        if (Directory.Exists(offlineDir))
        {
            var offline = Directory.EnumerateFiles(offlineDir, $"{namePrefix}*.zip").FirstOrDefault();
            if (offline is not null)
            {
                logger.LogInformation("使用離線安裝包: {Path}", offline);
                return offline;
            }
        }

        // 下載
        var target = Path.Combine(root, $"{namePrefix}.zip");
        if (File.Exists(target))
            return target;

        try
        {
            logger.LogInformation("下載 {Name}: {Url}", namePrefix, url);
            var http = httpClientFactory.CreateClient("neo4j-download");
            await using var stream = await http.GetStreamAsync(url, ct);
            await using var file = File.Create(target);
            await stream.CopyToAsync(file, ct);
            return target;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "下載失敗。企業無外網環境請將安裝包放入離線目錄並設定 Neo4jLifecycle:OfflinePackageDir");
            LastError = $"無法取得 {namePrefix} 安裝包。{GetOfflinePackageGuidance(_options)}";
            try { File.Delete(target); } catch { /* best effort */ }
            return null;
        }
    }

    private void ConfigureNeo4j(string homeDir)
    {
        var confPath = Path.Combine(homeDir, "conf", "neo4j.conf");
        if (!File.Exists(confPath))
            return;

        var lines = File.ReadAllLines(confPath).ToList();
        void SetConfig(string key, string value)
        {
            var idx = lines.FindIndex(l => l.TrimStart().StartsWith(key + "=") ||
                                           l.TrimStart().StartsWith("#" + key + "="));
            var line = $"{key}={value}";
            if (idx >= 0) lines[idx] = line;
            else lines.Add(line);
        }

        // 只綁 localhost，安全預設
        SetConfig("server.default_listen_address", "127.0.0.1");
        var boltPort = GetConfiguredBoltPort();
        SetConfig("server.bolt.listen_address", $"127.0.0.1:{boltPort}");
        SetConfig("server.bolt.advertised_address", $"127.0.0.1:{boltPort}");
        SetConfig("server.memory.heap.initial_size", "512m");
        SetConfig("server.memory.heap.max_size", "1g");
        SetConfig("server.memory.pagecache.size", "256m");
        File.WriteAllLines(confPath, lines);

        // 初始密碼只應在首次安裝設定。每次 Desktop 啟動都重寫 auth.ini
        // 會延長冷啟動，並可能與已在啟動中的 Neo4j 競爭同一認證檔。
        var authFile = Path.Combine(homeDir, "data", "dbms", "auth.ini");
        if (File.Exists(authFile))
            return;

        var javaExe = FindJavaExe(WingmanNeo4jRoot);
        if (javaExe is null)
            return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(homeDir, "bin", "neo4j-admin.bat"),
                Arguments = $"dbms set-initial-password {_neo4j.Password}",
                WorkingDirectory = homeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["JAVA_HOME"] = Path.GetDirectoryName(Path.GetDirectoryName(javaExe))!;
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30_000);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "設定初始密碼失敗（可能已設定過）");
        }
    }

    // ── 啟動 ──────────────────────────────────────────────────────────────────

    private async Task<bool> StartProcessAsync(string homeDir, CancellationToken ct)
    {
        var javaExe = FindJavaExe(WingmanNeo4jRoot);
        if (javaExe is null)
        {
            logger.LogError("找不到 JRE");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(homeDir, "bin", "neo4j.bat"),
                Arguments = "console",
                WorkingDirectory = homeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["JAVA_HOME"] = Path.GetDirectoryName(Path.GetDirectoryName(javaExe))!;

            _process = Process.Start(psi);
            if (_process is null)
                return false;

            _process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    logger.LogDebug("Neo4j: {Output}", args.Data);
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    logger.LogWarning("Neo4j: {Error}", args.Data);
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // 等待最多 90 秒直到可連線
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    logger.LogError("Neo4j 行程異常退出 (exit={Code})", _process.ExitCode);
                    return false;
                }
                if (await graphStore.PingAsync(ct))
                {
                    logger.LogInformation("Neo4j 啟動完成 (pid={Pid})", _process.Id);
                    await graphStore.EnsureSchemaAsync(ct);
                    return true;
                }
                await Task.Delay(2000, ct);
            }

            logger.LogError("Neo4j 啟動逾時");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Neo4j 啟動失敗");
            return false;
        }
    }

    private static string? FindNeo4jHome(string root)
    {
        if (!Directory.Exists(root))
            return null;
        return Directory.EnumerateDirectories(root, "neo4j-community-*")
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "neo4j.bat")));
    }

    private static string? FindJavaExe(string root)
    {
        var jreRoot = Path.Combine(root, "jre");
        if (!Directory.Exists(jreRoot))
            return null;
        return Directory.EnumerateFiles(jreRoot, "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains("bin", StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch { /* best effort */ }
        }
        _process?.Dispose();
        _startLock.Dispose();
    }

    private async Task<bool> VerifyManagedProcessAsync(CancellationToken ct)
    {
        if (await graphStore.PingAsync(ct))
        {
            Status = "running";
            LastError = null;
            return true;
        }

        Status = "unreachable";
        LastError = "Wingman 管理的 Neo4j 行程仍在執行，但無法在限定時間內回應。";
        logger.LogError("{Error}", LastError);
        return false;
    }

    private bool TryGetManagedEndpoint(out IPAddress address, out int port)
    {
        address = IPAddress.Loopback;
        port = 0;
        if (!Uri.TryCreate(_neo4j.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "bolt", StringComparison.OrdinalIgnoreCase) ||
            uri.Port is <= 0 or > 65535)
        {
            Status = "invalid-configuration";
            LastError = $"managed Neo4j URI 無效: {_neo4j.Uri}";
            logger.LogError("{Error}", LastError);
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
        }
        else if (string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.IPv6Loopback;
        }
        else
        {
            Status = "invalid-configuration";
            LastError =
                $"managed Neo4j 只能綁定 loopback 位址，實際設定為 {_neo4j.Uri}；" +
                "遠端或使用者管理的 Neo4j 請明確使用 external 模式。";
            logger.LogError("{Error}", LastError);
            return false;
        }

        port = uri.Port;
        return true;
    }

    internal static bool IsPortAvailable(IPAddress address, int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    internal static async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken ct)
    {
        var directory = WingmanNeo4jRoot;
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, "startup.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
    }

    private int GetConfiguredBoltPort()
    {
        if (Uri.TryCreate(_neo4j.Uri, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, "bolt", StringComparison.OrdinalIgnoreCase) &&
            uri.Port is > 0 and <= 65535)
        {
            return uri.Port;
        }

        logger.LogWarning(
            "Neo4j URI {Uri} 無法解析為有效的 Bolt URI，改用預設受管理連接埠 17688。",
            _neo4j.Uri);
        return 17688;
    }
}
