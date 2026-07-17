using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>
/// AGENTS.md 自動生成（WS3.4，Claude Code `/init` 等效）。
/// 掃描建置系統/測試框架 + 圖譜模組摘要 → LLM 彙整 → 寫入專案根目錄。
/// </summary>
public sealed class AgentsMdGenerator(
    ICodeGraphStore graphStore,
    ILlmCompletionService llm,
    ILogger<AgentsMdGenerator> logger)
{
    /// <summary>生成 AGENTS.md 並寫入專案根目錄，回傳內容。</summary>
    public async Task<string> GenerateAsync(
        string projectId, string projectRoot, CancellationToken ct = default)
    {
        // ── 1. 偵測建置系統與工具鏈 ────────────────────────────────────────
        var facts = DetectProjectFacts(projectRoot);

        // ── 2. 圖譜模組摘要（若有）───────────────────────────────────────
        var summaries = await graphStore.ListCommunitySummariesAsync(projectId, ct);
        var moduleSection = new StringBuilder();
        foreach (var s in summaries.Take(10))
        {
            moduleSection.AppendLine($"- **{s.Title}**: {s.Summary.Split('\n')[0]}");
        }

        // ── 3. LLM 彙整 ──────────────────────────────────────────────────
        var prompt = $"""
            你是資深工程師，請為以下專案撰寫一份精簡的 AGENTS.md
            （給 AI coding agent 閱讀的專案指南）。使用繁體中文。

            要求：
            - 只寫 agent 無法從程式碼推斷的資訊：建置/測試指令、架構決策、慣例
            - 每行都要有用，不寫廢話（"write clean code" 之類的不要）
            - 控制在 60 行內
            - 格式：# 專案概述 / # 建置與測試 / # 架構 / # 慣例

            偵測到的專案事實：
            {facts}

            模組摘要（圖譜分析結果）：
            {(moduleSection.Length > 0 ? moduleSection.ToString() : "（無）")}

            只輸出 AGENTS.md 內容，不要其他前後綴。
            """;

        var content = await llm.CompleteAsync(
            prompt,
            new LlmTelemetryContext(
                FeatureArea: "agents_md_generation",
                ProjectId: projectId),
            ct);

        // ── 4. 寫入 ──────────────────────────────────────────────────────
        var path = Path.Combine(projectRoot, "AGENTS.md");
        await File.WriteAllTextAsync(path, content, ct);
        logger.LogInformation("AGENTS.md 已生成: {Path}", path);

        return content;
    }

    /// <summary>偵測建置系統、測試框架、工具鏈等專案事實。</summary>
    internal static string DetectProjectFacts(string root)
    {
        var facts = new StringBuilder();

        void Check(string pattern, string fact)
        {
            try
            {
                if (Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any())
                    facts.AppendLine($"- {fact}");
            }
            catch { /* ignore */ }
        }

        bool ExistsAnyDepth(string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 3,
                    IgnoreInaccessible = true,
                }).Any();
            }
            catch
            {
                return false;
            }
        }

        // .NET
        Check("*.sln", "含 .NET solution（dotnet build / dotnet test）");
        if (ExistsAnyDepth("*.csproj"))
            facts.AppendLine("- 含 C# 專案（dotnet build）");

        // Java
        Check("pom.xml", "Maven 專案（mvn compile / mvn test）");
        Check("build.gradle", "Gradle 專案（gradle build / gradle test）");
        Check("build.gradle.kts", "Gradle Kotlin DSL 專案");

        // JS/TS
        Check("package.json", "Node.js 專案（檢查 package.json scripts）");
        Check("pnpm-workspace.yaml", "pnpm monorepo");

        // 其他訊號
        Check("Dockerfile", "含 Dockerfile");
        Check("docker-compose.yml", "含 docker-compose");
        Check(".editorconfig", "有 .editorconfig 編碼慣例");
        if (Directory.Exists(Path.Combine(root, ".github", "workflows")))
            facts.AppendLine("- 有 GitHub Actions CI");
        if (Directory.Exists(Path.Combine(root, ".git")))
            facts.AppendLine("- Git 版本控制");

        return facts.Length > 0 ? facts.ToString() : "（未偵測到已知建置系統）";
    }
}
