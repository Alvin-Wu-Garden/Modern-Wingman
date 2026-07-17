using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.Workflow;

/// <summary>單次驗證結果。</summary>
public sealed record VerificationResult(bool Success, string Command, string Output);

/// <summary>
/// 驗證服務（WS4：Verify 階段）。
/// 依專案類型自動選擇建置/測試指令，回傳可讀輸出供 Agent 迭代修復。
/// </summary>
public sealed class VerificationService(ILogger<VerificationService> logger)
{
    /// <summary>
    /// 偵測專案的驗證指令（建置優先，其次測試）。
    /// </summary>
    public static List<string> DetectVerifyCommands(string projectRoot)
    {
        var commands = new List<string>();

        bool HasFile(string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(projectRoot, pattern, SearchOption.TopDirectoryOnly).Any();
            }
            catch
            {
                return false;
            }
        }

        if (HasFile("*.sln") || HasFile("*.csproj"))
        {
            commands.Add("dotnet build");
            commands.Add("dotnet test --no-build");
        }
        else if (HasFile("pom.xml"))
        {
            commands.Add("mvn -q compile");
            commands.Add("mvn -q test");
        }
        else if (HasFile("build.gradle") || HasFile("build.gradle.kts"))
        {
            commands.Add("gradle build -q");
        }
        else if (HasFile("package.json"))
        {
            commands.Add("npm run build --if-present");
            commands.Add("npm test --if-present");
        }

        return commands;
    }

    /// <summary>
    /// 執行一條驗證指令（shell），回傳成功與否 + 截斷輸出。
    /// </summary>
    public async Task<VerificationResult> RunAsync(
        string projectRoot, string command, CancellationToken ct = default)
    {
        logger.LogInformation("執行驗證: {Command}（{Root}）", command, projectRoot);

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command}\"",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            return new VerificationResult(false, command, "無法啟動驗證行程");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 10 分鐘上限
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new VerificationResult(false, command, "驗證逾時（10 分鐘）");
        }

        var success = proc.ExitCode == 0;
        var output = TruncateOutput(stdout.ToString(), stderr.ToString(), success);
        logger.LogInformation("驗證{Result}: {Command}", success ? "通過" : "失敗", command);
        return new VerificationResult(success, command, output);
    }

    /// <summary>失敗時保留錯誤訊息尾部（最相關），成功時只留摘要。</summary>
    internal static string TruncateOutput(string stdout, string stderr, bool success)
    {
        if (success)
        {
            var lines = stdout.Split('\n');
            return lines.Length <= 5 ? stdout.Trim() : string.Join('\n', lines[^5..]).Trim();
        }

        var combined = (stdout + "\n" + stderr).Trim();
        const int maxChars = 4000;
        return combined.Length <= maxChars ? combined : "…" + combined[^maxChars..];
    }
}
