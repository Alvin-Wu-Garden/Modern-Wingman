namespace AgentService.Application.Models;

/// <summary>
/// 啟動 Git／SVN 程序所需的最小參數。
/// ArgumentList 由執行器逐項傳入，不經 shell 字串解析。
/// </summary>
public sealed record ProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? Environment = null,
    int MaxOutputCharacters = 1_000_000,
    string? StandardInput = null,
    Func<ProcessOutputLine, CancellationToken, ValueTask>? OnOutput = null);

/// <summary>單行標準輸出或錯誤輸出，供遠端專案匯入進度顯示。</summary>
public sealed record ProcessOutputLine(
    bool IsError,
    string Text,
    DateTimeOffset Timestamp);

/// <summary>外部程序完成後的結果；輸出已套用大小上限。</summary>
public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    long DurationMs);
