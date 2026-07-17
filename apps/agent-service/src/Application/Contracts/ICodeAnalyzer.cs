using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 語言分析器（Strategy 模式）：把某語言的原始碼解析為統一的 CodeNode/CodeEdge。
/// OCP：支援新語言 = 新增一個 ICodeAnalyzer 實作並註冊 DI。
/// </summary>
public interface ICodeAnalyzer
{
    /// <summary>"csharp" | "java"</summary>
    string Language { get; }

    /// <summary>此分析器可處理的副檔名（".cs"）。</summary>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>分析指定檔案集合（路徑為絕對路徑），回傳統一圖譜模型。</summary>
    Task<CodeAnalysisResult> AnalyzeAsync(
        string projectRoot,
        IReadOnlyList<string> files,
        CancellationToken ct = default);
}
