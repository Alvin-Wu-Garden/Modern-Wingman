using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.VersionControl;

/// <summary>
/// 只供 Git／SVN 專案匯入使用的受控程序執行器。
/// 不經 Shell 解譯命令，參數逐一加入 ArgumentList，避免路徑或帳密被當成命令執行。
/// </summary>
public sealed class ManagedProcessRunner(ILogger<ManagedProcessRunner> logger) : IProcessRunner
{
    private static readonly string[] InheritedEnvironmentVariables =
    [
        "PATH", "PATHEXT", "SystemRoot", "SYSTEMDRIVE", "TEMP", "TMP",
        "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA",
    ];

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessInvocation invocation,
        CancellationToken ct = default)
    {
        Validate(invocation);
        var startInfo = BuildStartInfo(invocation);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new BoundedTextBuffer(invocation.MaxOutputCharacters);
        var stderr = new BoundedTextBuffer(invocation.MaxOutputCharacters);
        var stopwatch = Stopwatch.StartNew();
        var channel = invocation.OnOutput is null
            ? null
            : Channel.CreateUnbounded<ProcessOutputLine>(
                new UnboundedChannelOptions { SingleReader = true });
        var outputTask = channel is null
            ? Task.CompletedTask
            : ConsumeOutputAsync(channel.Reader, invocation.OnOutput!, ct);

        process.OutputDataReceived += (_, args) =>
        {
            stdout.AppendLine(args.Data);
            if (args.Data is not null)
                channel?.Writer.TryWrite(new(false, args.Data, DateTimeOffset.UtcNow));
        };
        process.ErrorDataReceived += (_, args) =>
        {
            stderr.AppendLine(args.Data);
            if (args.Data is not null)
                channel?.Writer.TryWrite(new(true, args.Data, DateTimeOffset.UtcNow));
        };

        if (!process.Start())
            throw new InvalidOperationException($"無法啟動程序：{invocation.FileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (invocation.StandardInput is not null)
            await process.StandardInput.WriteLineAsync(invocation.StandardInput);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(invocation.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            timedOut = true;
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            channel?.Writer.TryComplete();
            try
            {
                await outputTask;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        logger.LogInformation(
            "VCS 程序完成：{FileName}，ExitCode={ExitCode}，TimedOut={TimedOut}，DurationMs={DurationMs}",
            invocation.FileName,
            timedOut ? -1 : process.ExitCode,
            timedOut,
            stopwatch.ElapsedMilliseconds);
        return new(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            stopwatch.ElapsedMilliseconds);
    }

    private static ProcessStartInfo BuildStartInfo(ProcessInvocation invocation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            WorkingDirectory = Path.GetFullPath(invocation.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment.Clear();
        foreach (var name in InheritedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[name] = value;
        }
        foreach (var (name, value) in invocation.Environment ??
                                      new Dictionary<string, string?>())
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0'))
                throw new ArgumentException("環境變數名稱不合法。", nameof(invocation));
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }
        return startInfo;
    }

    private static void Validate(ProcessInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.FileName))
            throw new ArgumentException("必須指定 VCS 執行檔。", nameof(invocation));
        if (!Directory.Exists(invocation.WorkingDirectory))
            throw new DirectoryNotFoundException(invocation.WorkingDirectory);
        if (invocation.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(invocation), "逾時必須大於零。");
    }

    private static async Task ConsumeOutputAsync(
        ChannelReader<ProcessOutputLine> reader,
        Func<ProcessOutputLine, CancellationToken, ValueTask> callback,
        CancellationToken ct)
    {
        await foreach (var line in reader.ReadAllAsync(ct))
            await callback(line, ct);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class BoundedTextBuffer(int capacity)
    {
        private readonly StringBuilder _builder = new(Math.Min(capacity, 4096));
        private readonly object _gate = new();
        private bool _truncated;

        public void AppendLine(string? value)
        {
            if (value is null)
                return;
            lock (_gate)
            {
                var remaining = capacity - _builder.Length;
                if (remaining <= 0)
                {
                    _truncated = true;
                    return;
                }
                var line = value + Environment.NewLine;
                _builder.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
                _truncated |= line.Length > remaining;
            }
        }

        public override string ToString()
        {
            lock (_gate)
                return _truncated
                    ? _builder + Environment.NewLine + "[output truncated]"
                    : _builder.ToString();
        }
    }
}
