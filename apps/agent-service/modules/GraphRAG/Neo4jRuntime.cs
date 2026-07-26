using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// Neo4j runtime 模式與離線安裝設定。
/// managed 只允許 Wingman 自己啟動的 loopback process；external 只驗證使用者管理的 instance；
/// disabled 完全不建立連線或下載檔案。
/// </summary>
public sealed class GraphRagNeo4jRuntimeOptions
{
    /// <summary>沿用既有 Neo4jLifecycle 設定 section。</summary>
    public const string SectionName = "Neo4jLifecycle";

    /// <summary>managed、external 或 disabled。</summary>
    public string Mode { get; set; } = "managed";

    /// <summary>managed 模式的 Neo4j Community Windows zip URL。</summary>
    public string DownloadUrl { get; set; } =
        "https://dist.neo4j.org/neo4j-community-5.26.0-windows.zip";

    /// <summary>managed 模式的 JRE 21 Windows zip URL。</summary>
    public string JreDownloadUrl { get; set; } =
        "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse";

    /// <summary>企業離線安裝包目錄；null 時使用本機 Wingman packages 目錄。</summary>
    public string? OfflinePackageDir { get; set; }
}

/// <summary>索引與 API 共用的 Neo4j runtime 可用性契約。</summary>
public interface INeo4jRuntime
{
    /// <summary>目前 lifecycle 狀態。</summary>
    string Status { get; }

    /// <summary>最近一次去敏感錯誤。</summary>
    string? LastError { get; }

    /// <summary>確保 store 已可連線且 schema ready。</summary>
    Task<bool> EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 管理內建 Neo4j Community 的安裝與 process 生命週期。
/// 任何索引前都應呼叫 <see cref="EnsureAvailableAsync"/>；失敗只阻止新發布，不得刪除上一個 active graph。
/// </summary>
public sealed class Neo4jRuntime : INeo4jRuntime, IAsyncDisposable
{
    private readonly GraphRagNeo4jRuntimeOptions _runtime;
    private readonly GraphRagNeo4jOptions _neo4j;
    private readonly IGraphStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<Neo4jRuntime> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;

    /// <summary>建立 managed/external/disabled runtime。</summary>
    public Neo4jRuntime(
        IOptions<GraphRagNeo4jRuntimeOptions> runtime,
        IOptions<GraphRagNeo4jOptions> neo4j,
        IGraphStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<Neo4jRuntime> logger)
    {
        _runtime = runtime.Value;
        _neo4j = neo4j.Value;
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>running、stopped、disabled、unreachable、install-failed 等可顯示狀態。</summary>
    public string Status { get; private set; } = "stopped";

    /// <summary>最近一次 lifecycle 失敗的去敏感繁體中文說明。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 確保 V3 store 可用。managed 模式不會把同 port 的 Docker／外部 Neo4j 誤認成 Wingman process。
    /// </summary>
    /// <param name="progress">可選安裝進度。</param>
    /// <param name="cancellationToken">取消安裝或啟動的 token。</param>
    /// <returns>可安全讀寫 V3 graph 時為 true。</returns>
    public async Task<bool> EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;
        var mode = _runtime.Mode.Trim().ToLowerInvariant();
        if (mode == "disabled")
        {
            Status = "disabled";
            return false;
        }
        if (mode == "external")
        {
            if (await _store.PingAsync(cancellationToken))
            {
                await _store.EnsureSchemaAsync(cancellationToken);
                Status = "running";
                return true;
            }
            Status = "unreachable";
            LastError = "外部 Neo4j 無法連線；請檢查服務狀態與連線設定。";
            return false;
        }
        if (mode != "managed")
        {
            Status = "invalid-configuration";
            LastError = "Neo4j lifecycle mode 只允許 managed、external 或 disabled。";
            return false;
        }
        if (!OperatingSystem.IsWindows())
        {
            Status = "unsupported-platform";
            LastError = "目前內建 Neo4j managed runtime 只支援 Windows；其他平台請使用 external 模式。";
            return false;
        }
        if (_process is { HasExited: false })
            return await VerifyManagedProcessAsync(cancellationToken);

        await _startGate.WaitAsync(cancellationToken);
        FileStream? crossProcessLock = null;
        try
        {
            crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken);
            if (_process is { HasExited: false })
                return await VerifyManagedProcessAsync(cancellationToken);
            if (!TryGetManagedEndpoint(out var address, out var port))
                return false;
            if (!IsPortAvailable(address, port))
            {
                Status = "port-conflict";
                LastError =
                    $"Wingman 管理的 Neo4j 連接埠 {address}:{port} 已被其他程序占用；" +
                    "為避免誤用外部資料庫，managed 模式已拒絕啟動。";
                return false;
            }

            Status = "installing";
            var home = await EnsureInstalledAsync(progress, cancellationToken);
            if (home is null)
            {
                Status = "install-failed";
                return false;
            }
            Status = "starting";
            progress?.Report("正在啟動 Neo4j V3 runtime...");
            var started = await StartProcessAsync(home, cancellationToken);
            Status = started ? "running" : "start-failed";
            LastError ??= started ? null : "Wingman 管理的 Neo4j 無法啟動。";
            return started;
        }
        finally
        {
            if (crossProcessLock is not null)
                await crossProcessLock.DisposeAsync();
            _startGate.Release();
        }
    }

    /// <summary>取得實際離線安裝包目錄，供 UI 顯示操作指引。</summary>
    public static string GetEffectiveOfflinePackageDirectory(
        GraphRagNeo4jRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.OfflinePackageDir)
            ? Path.Combine(RuntimeRoot, "packages")
            : Environment.ExpandEnvironmentVariables(options.OfflinePackageDir);
    }

    /// <summary>產生不含帳密的離線安裝指引。</summary>
    public static string GetOfflinePackageGuidance(
        GraphRagNeo4jRuntimeOptions options)
    {
        var directory = GetEffectiveOfflinePackageDirectory(options);
        return
            $"請將離線安裝包放入 {directory}；檔名需符合 " +
            "neo4j-community*.zip 與 jre*.zip，或設定 Neo4jLifecycle:OfflinePackageDir。";
    }

    /// <summary>檢查 managed port 是否未被其他 process 監聽。</summary>
    public static bool IsPortAvailable(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
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

    /// <summary>
    /// 取得 process-crash-safe 檔案鎖。FileShare.None 不具 thread affinity，
    /// 可安全跨 await continuation 使用，並防止兩個 Desktop instance 同時安裝／啟動。
    /// </summary>
    public static async Task<FileStream> AcquireCrossProcessLockAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RuntimeRoot);
        var path = Path.Combine(RuntimeRoot, "startup-v3.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task<string?> EnsureInstalledAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Neo4j managed runtime 只支援 Windows。");
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(GetEffectiveOfflinePackageDirectory(_runtime));
        var home = FindNeo4jHome(RuntimeRoot);
        if (home is null)
        {
            progress?.Report("正在取得 Neo4j Community...");
            var package = await AcquirePackageAsync(
                "neo4j-community", _runtime.DownloadUrl, cancellationToken);
            if (package is null) return null;
            progress?.Report("正在解壓 Neo4j Community...");
            ZipFile.ExtractToDirectory(package, RuntimeRoot, overwriteFiles: true);
            home = FindNeo4jHome(RuntimeRoot);
        }
        if (FindJavaExecutable(RuntimeRoot) is null)
        {
            progress?.Report("正在取得 JRE 21...");
            var package = await AcquirePackageAsync(
                "jre", _runtime.JreDownloadUrl, cancellationToken);
            if (package is null) return null;
            var jreDirectory = Path.Combine(RuntimeRoot, "jre");
            Directory.CreateDirectory(jreDirectory);
            progress?.Report("正在解壓 JRE 21...");
            ZipFile.ExtractToDirectory(package, jreDirectory, overwriteFiles: true);
        }
        if (home is null)
        {
            LastError = "Neo4j 安裝包解壓後找不到可執行檔。";
            return null;
        }
        ConfigureNeo4j(home);
        return home;
    }

    private async Task<string?> AcquirePackageAsync(
        string prefix,
        string url,
        CancellationToken cancellationToken)
    {
        var offlineDirectory = GetEffectiveOfflinePackageDirectory(_runtime);
        if (Directory.Exists(offlineDirectory))
        {
            var offline = Directory.EnumerateFiles(
                    offlineDirectory, $"{prefix}*.zip")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (offline is not null)
            {
                _logger.LogInformation(
                    "使用 Neo4j 離線安裝包。PackageType={PackageType}", prefix);
                return offline;
            }
        }

        var target = Path.Combine(RuntimeRoot, $"{prefix}.zip");
        if (File.Exists(target)) return target;
        try
        {
            var client = _httpClientFactory.CreateClient("neo4j-download");
            await using var source = await client.GetStreamAsync(url, cancellationToken);
            var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var output = new FileStream(
                                 temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                    await source.CopyToAsync(output, cancellationToken);
                File.Move(temporary, target);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Neo4j 安裝包下載失敗。PackageType={PackageType}, ExceptionType={ExceptionType}",
                prefix, exception.GetType().Name);
            LastError = $"無法取得 {prefix} 安裝包。{GetOfflinePackageGuidance(_runtime)}";
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private void ConfigureNeo4j(string home)
    {
        var configurationPath = Path.Combine(home, "conf", "neo4j.conf");
        if (!File.Exists(configurationPath)) return;
        var lines = File.ReadAllLines(configurationPath).ToList();
        SetConfiguration(lines, "server.default_listen_address", "127.0.0.1");
        var port = ManagedPort();
        SetConfiguration(lines, "server.bolt.listen_address", $"127.0.0.1:{port}");
        SetConfiguration(lines, "server.bolt.advertised_address", $"127.0.0.1:{port}");
        SetConfiguration(lines, "server.memory.heap.initial_size", "512m");
        SetConfiguration(lines, "server.memory.heap.max_size", "1g");
        SetConfiguration(lines, "server.memory.pagecache.size", "256m");
        File.WriteAllLines(configurationPath, lines);

        var authFile = Path.Combine(home, "data", "dbms", "auth.ini");
        if (File.Exists(authFile)) return;
        var java = FindJavaExecutable(RuntimeRoot);
        if (java is null)
        {
            LastError = "Neo4j 初始認證設定找不到 JRE。";
            return;
        }
        var admin = Path.Combine(home, "bin", "neo4j-admin.bat");
        var start = new ProcessStartInfo
        {
            FileName = admin,
            Arguments = $"dbms set-initial-password {_neo4j.Password}",
            WorkingDirectory = home,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["JAVA_HOME"] =
            Path.GetDirectoryName(Path.GetDirectoryName(java))!;
        using var process = Process.Start(start);
        if (process is null || !process.WaitForExit(30_000) || process.ExitCode != 0)
            LastError = "Neo4j 初始認證設定失敗。";
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> StartProcessAsync(
        string home,
        CancellationToken cancellationToken)
    {
        var java = FindJavaExecutable(RuntimeRoot);
        if (java is null)
        {
            LastError = "Neo4j runtime 找不到 JRE。";
            return false;
        }
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(home, "bin", "neo4j.bat"),
                Arguments = "console",
                WorkingDirectory = home,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["JAVA_HOME"] =
                Path.GetDirectoryName(Path.GetDirectoryName(java))!;
            _process = Process.Start(start);
            if (_process is null) return false;
            _process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogDebug("Neo4j runtime: {Output}", args.Data);
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogWarning("Neo4j runtime: {Output}", args.Data);
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    LastError = "Neo4j process 在啟動期間異常退出。";
                    return false;
                }
                if (await _store.PingAsync(cancellationToken))
                {
                    await _store.EnsureSchemaAsync(cancellationToken);
                    return true;
                }
                await Task.Delay(2_000, cancellationToken);
            }
            LastError = "Neo4j 啟動超過 90 秒仍無法連線。";
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Neo4j process 啟動失敗。ExceptionType={ExceptionType}",
                exception.GetType().Name);
            LastError = "Neo4j process 啟動失敗。";
            return false;
        }
    }

    private async Task<bool> VerifyManagedProcessAsync(
        CancellationToken cancellationToken)
    {
        if (await _store.PingAsync(cancellationToken))
        {
            Status = "running";
            LastError = null;
            return true;
        }
        Status = "unreachable";
        LastError = "Wingman 管理的 Neo4j process 仍存在，但無法在連線逾時內回應。";
        return false;
    }

    private bool TryGetManagedEndpoint(out IPAddress address, out int port)
    {
        address = IPAddress.Loopback;
        port = 0;
        if (!Uri.TryCreate(_neo4j.Uri, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("bolt", StringComparison.OrdinalIgnoreCase) ||
            uri.Port is <= 0 or > 65535)
        {
            Status = "invalid-configuration";
            LastError = "managed Neo4j URI 必須是有效的 bolt loopback URI。";
            return false;
        }
        if (uri.Host is "localhost" or "127.0.0.1")
            address = IPAddress.Loopback;
        else if (uri.Host == "::1")
            address = IPAddress.IPv6Loopback;
        else
        {
            Status = "invalid-configuration";
            LastError = "managed Neo4j 只能綁定 loopback；遠端資料庫請使用 external 模式。";
            return false;
        }
        port = uri.Port;
        return true;
    }

    private int ManagedPort() =>
        Uri.TryCreate(_neo4j.Uri, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("bolt", StringComparison.OrdinalIgnoreCase) &&
        uri.Port is > 0 and <= 65535
            ? uri.Port
            : 17688;

    private static void SetConfiguration(
        IList<string> lines,
        string key,
        string value)
    {
        var index = lines
            .Select((line, position) => (line, position))
            .FirstOrDefault(item =>
                item.line.TrimStart().StartsWith($"{key}=", StringComparison.Ordinal) ||
                item.line.TrimStart().StartsWith($"#{key}=", StringComparison.Ordinal))
            .position;
        var replacement = $"{key}={value}";
        if (index > 0 || lines.Count > 0 &&
            (lines[0].TrimStart().StartsWith($"{key}=", StringComparison.Ordinal) ||
             lines[0].TrimStart().StartsWith($"#{key}=", StringComparison.Ordinal)))
            lines[index] = replacement;
        else
            lines.Add(replacement);
    }

    private static string? FindNeo4jHome(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "neo4j-community-*")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(path =>
                    File.Exists(Path.Combine(path, "bin", "neo4j.bat")))
            : null;

    private static string? FindJavaExecutable(string root)
    {
        var jre = Path.Combine(root, "jre");
        return Directory.Exists(jre)
            ? Directory.EnumerateFiles(jre, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                    path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wingman",
        "neo4j");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // App 結束時是 best effort；OS 最終仍會回收 child process。
            }
        }
        _process?.Dispose();
        _startGate.Dispose();
    }
}

/// <summary>
/// 管理內建 Neo4j 密碼。Windows 使用 CurrentUser DPAPI；環境變數永遠優先，
/// 使企業部署能由外部 secret manager 注入且不必把密碼寫進 appsettings。
/// </summary>
public static class GraphRagNeo4jCredentialStore
{
    private const string EnvironmentVariableName = "WINGMAN_NEO4J_PASSWORD";
    private const string FileName = "neo4j-password.dpapi";
    private const string Scheme = "dpapi-current-user-v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "ModernWingman.Neo4j.LocalCredential.v1");

    /// <summary>依環境變數、設定、DPAPI 檔案的順序解析密碼。</summary>
    public static string Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var environmentPassword =
            Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(environmentPassword))
            return environmentPassword;
        var configuredPassword = configuration["Neo4j:Password"];
        if (!string.IsNullOrWhiteSpace(configuredPassword))
            return configuredPassword;
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                $"Neo4j password 未設定；請透過 {EnvironmentVariableName} 注入。");
        var path = CredentialPath();
        if (File.Exists(path)) return Read(path);
        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Write(path, generated);
        return generated;
    }

    private static string CredentialPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWingman",
        "secrets",
        FileName);

    [SupportedOSPlatform("windows")]
    private static string Read(string path)
    {
        var payload = File.ReadAllText(path, Encoding.UTF8);
        var separator = payload.IndexOf(':');
        if (separator <= 0 ||
            !payload[..separator].Equals(Scheme, StringComparison.Ordinal))
            throw new InvalidOperationException("本機 Neo4j credential 格式無效。");
        try
        {
            var encrypted = Convert.FromBase64String(payload[(separator + 1)..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                encrypted, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "目前 Windows 使用者無法解密本機 Neo4j credential。",
                exception);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Write(string path, string password)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password),
            Entropy,
            DataProtectionScope.CurrentUser);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                $"{Scheme}:{Convert.ToBase64String(encrypted)}",
                Encoding.UTF8);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
