using System.Diagnostics;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using System.Threading.Channels;

namespace AgentService.Infrastructure.Tools;

public sealed class ManagedProcessRunner(ILogger<ManagedProcessRunner> logger) : IProcessRunner
{
    private static readonly string[] InheritedEnvironmentVariables =
    [
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "SYSTEMDRIVE",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "HOME",
        "LOCALAPPDATA",
        "APPDATA",
        "PROGRAMDATA",
        "DOTNET_ROOT",
        "NUGET_PACKAGES",
        "JAVA_HOME",
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
        var outputChannel = invocation.OnOutput is null
            ? null
            : Channel.CreateUnbounded<ProcessOutputLine>(new UnboundedChannelOptions { SingleReader = true });
        var outputTask = outputChannel is null
            ? Task.CompletedTask
            : ConsumeOutputAsync(outputChannel.Reader, invocation.OnOutput!, ct);

        process.OutputDataReceived += (_, e) =>
        {
            stdout.AppendLine(e.Data);
            if (e.Data is not null) outputChannel?.Writer.TryWrite(new(false, e.Data, DateTimeOffset.UtcNow));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            stderr.AppendLine(e.Data);
            if (e.Data is not null) outputChannel?.Writer.TryWrite(new(true, e.Data, DateTimeOffset.UtcNow));
        };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {invocation.FileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (invocation.StandardInput is not null)
            await process.StandardInput.WriteLineAsync(invocation.StandardInput);
        process.StandardInput.Close();

        using var timeoutCts = new CancellationTokenSource(invocation.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCts.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            timedOut = true;
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            outputChannel?.Writer.TryComplete();
            try { await outputTask; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        logger.LogInformation(
            "Process completed: {FileName}, exit={ExitCode}, timeout={TimedOut}, duration={DurationMs}ms",
            invocation.FileName,
            timedOut ? -1 : process.ExitCode,
            timedOut,
            stopwatch.ElapsedMilliseconds);

        return new ProcessExecutionResult(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            stopwatch.ElapsedMilliseconds);
    }

    private static async Task ConsumeOutputAsync(
        ChannelReader<ProcessOutputLine> reader,
        Func<ProcessOutputLine, CancellationToken, ValueTask> callback,
        CancellationToken ct)
    {
        await foreach (var line in reader.ReadAllAsync(ct))
            await callback(line, ct);
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

        if (invocation.Environment is not null)
        {
            foreach (var (name, value) in invocation.Environment)
            {
                ValidateEnvironmentVariableName(name);
                if (value is null)
                    startInfo.Environment.Remove(name);
                else
                    startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    private static void Validate(ProcessInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.FileName))
            throw new ArgumentException("Executable is required.", nameof(invocation));
        if (!Directory.Exists(invocation.WorkingDirectory))
            throw new DirectoryNotFoundException(invocation.WorkingDirectory);
        if (invocation.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(invocation), "Timeout must be positive.");
        if (invocation.MaxOutputCharacters is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(
                nameof(invocation),
                "Max output must be between 1 and 10,000,000 characters.");
    }

    private static void ValidateEnvironmentVariableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('=') ||
            name.Contains('\0'))
        {
            throw new ArgumentException("Invalid environment variable name.", nameof(name));
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process exited between the state check and Kill.
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
                if (_builder.Length >= capacity)
                {
                    _truncated = true;
                    return;
                }

                var remaining = capacity - _builder.Length;
                var text = value.Length + Environment.NewLine.Length <= remaining
                    ? value + Environment.NewLine
                    : value[..Math.Max(0, remaining)];
                _builder.Append(text);
                _truncated |= text.Length < value.Length + Environment.NewLine.Length;
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _truncated
                    ? _builder + Environment.NewLine + "[output truncated]"
                    : _builder.ToString();
            }
        }
    }
}
