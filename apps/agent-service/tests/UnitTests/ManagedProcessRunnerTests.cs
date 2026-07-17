using AgentService.Application.Models;
using AgentService.Infrastructure.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class ManagedProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_PreservesArgumentsAndCapturesOutput()
    {
        var runner = new ManagedProcessRunner(NullLogger<ManagedProcessRunner>.Instance);
        var result = await runner.RunAsync(new ProcessInvocation(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('hello world')"],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(10)));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello world", result.StandardOutput);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_StopsProcessAtTimeout()
    {
        var runner = new ManagedProcessRunner(NullLogger<ManagedProcessRunner>.Instance);
        var result = await runner.RunAsync(new ProcessInvocation(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 5"],
            Path.GetTempPath(),
            TimeSpan.FromMilliseconds(250)));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.DurationMs < 5_000);
    }

    [Fact]
    public async Task RunAsync_DoesNotInheritUnlistedSecrets()
    {
        const string name = "WINGMAN_PROCESS_RUNNER_SECRET_TEST";
        Environment.SetEnvironmentVariable(name, "must-not-leak");
        try
        {
            var runner = new ManagedProcessRunner(NullLogger<ManagedProcessRunner>.Instance);
            var result = await runner.RunAsync(new ProcessInvocation(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", $"[Console]::Write($env:{name})"],
                Path.GetTempPath(),
                TimeSpan.FromSeconds(10)));

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("must-not-leak", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task RunAsync_StreamsStdoutAndStderrBeforeCompletion()
    {
        var streamed = new List<ProcessOutputLine>();
        var runner = new ManagedProcessRunner(NullLogger<ManagedProcessRunner>.Instance);
        var result = await runner.RunAsync(new ProcessInvocation(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'out-line'; [Console]::Error.WriteLine('err-line')"],
            Path.GetTempPath(),
            TimeSpan.FromSeconds(10),
            OnOutput: (line, _) =>
            {
                streamed.Add(line);
                return ValueTask.CompletedTask;
            }));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(streamed, line => !line.IsError && line.Text == "out-line");
        Assert.Contains(streamed, line => line.IsError && line.Text == "err-line");
    }
}
