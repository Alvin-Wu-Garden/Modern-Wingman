using System.Text;
using System.Security.Cryptography;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;
using AgentService.Modules.GraphRAG;
using V3GraphConfidence = AgentService.Modules.GraphRAG.GraphConfidence;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>
/// 將 GraphRAG V3 檢索結果收斂成有界、可追溯的 Evidence Pack。
/// 這是 P2 的 deterministic data plane；LLM 只能根據回傳 evidence 作解釋，
/// 不負責猜測哪一個圖譜工具應被使用。
/// </summary>
public sealed class ProjectEvidencePlanner(
    GraphRetrievalService graph,
    IEvidencePackBuilder packBuilder,
    ProjectDataEvidencePlanner dataEvidencePlanner,
    ISensitiveDataRedactor redactor)
{
    public async Task<EvidencePack> BuildAsync(
        ProjectEntity project,
        ChangeBrief brief,
        CancellationToken ct = default)
    {
        var evidence = new List<EvidenceItem>();
        var paths = new List<EvidencePath>();
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        var graphEvidenceCount = 0;

        foreach (var target in brief.Targets.Where(target =>
                     target.Kind is ChangeTargetKind.GitDiff or ChangeTargetKind.ErrorLog))
        {
            var excerpt = RedactedBounded(target.Value);
            evidence.Add(new EvidenceItem(
                $"user-input:{Hash(excerpt)}",
                target.Kind.ToString(),
                target.Kind == ChangeTargetKind.GitDiff ? "IT 提供的 Git diff" : "IT 提供的錯誤紀錄",
                EvidenceConfidence.Exact,
                "user-input",
                Excerpt: excerpt,
                Reason: "Exact 表示內容忠實保留自使用者輸入，不表示其中的推論已被程式碼圖譜驗證。",
                Relevance: 100));
        }

        var queries = brief.Targets
            .Select(target => target.Kind is ChangeTargetKind.NaturalLanguage or ChangeTargetKind.ErrorLog or ChangeTargetKind.GitDiff
                ? ExtractSearchTerms(target.Value)
                : [target.Value])
            .SelectMany(terms => terms)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        foreach (var query in queries)
        {
            var context = await graph.LocalSearchAsync(project.Id, query, ct);
            foreach (var hit in context.Nodes)
            {
                if (!seenNodes.Add(hit.Node.Id))
                    continue;

                evidence.Add(await ToEvidenceAsync(
                    project.RootPath,
                    hit,
                    relevance: Score(hit, query),
                    ct));
                graphEvidenceCount++;
            }

            foreach (var edge in context.Edges.Where(edge => seenEdges.Add(edge.Id)).Take(40))
            {
                var confidence = StrongestConfidence(edge.Evidence);
                paths.Add(new EvidencePath(
                    edge.Kind.ToString(),
                    [$"node:{edge.SourceId}", $"node:{edge.TargetId}"],
                    ToEvidenceConfidence(confidence),
                    context.Diagnostics.Count > 0));
            }
        }

        var dataEvidence = await dataEvidencePlanner.BuildAsync(brief, ct);
        evidence.AddRange(dataEvidence.Items);

        var gaps = new List<string>(dataEvidence.CapabilityGaps);
        if (queries.Count == 0)
            gaps.Add("未提供可用的程式碼目標；目前只能依自然語言建立初步假設。");
        if (graphEvidenceCount == 0)
            gaps.Add("GraphRAG V3 未找到可驗證的相符實體；可能需要重新索引或補充功能名稱、檔案、類型、Route 或錯誤 log。");
        if (brief.Classification.AnalysisMode == ChangeAnalysisMode.ImpactAnalysis)
            gaps.Add("目前影響分析主要根據靜態程式呼叫關係；資料庫、即時設定與外部系統證據需由對應 capability 提供。");

        return packBuilder.Build(new EvidencePackRequest(
            brief,
            evidence,
            paths,
            ToFreshness(project.IndexStatus),
            ManifestVersion(project),
            CapabilityGaps: gaps));
    }

    public static string FormatForPrompt(EvidencePack pack)
    {
        var prompt = new StringBuilder("\n\n## Project Change Evidence\n");
        prompt.AppendLine($"Index freshness: {pack.Freshness}; manifest: {pack.ManifestVersion ?? "unknown"}");
        prompt.AppendLine("Only treat Exact/Resolved evidence as verified facts. Label all other conclusions as inference or unknown.");
        foreach (var item in pack.Items)
        {
            prompt.Append("- [").Append(item.Confidence).Append("] ").Append(item.Summary);
            if (!string.IsNullOrWhiteSpace(item.FilePath))
                prompt.Append(" (").Append(item.FilePath).Append(item.StartLine is null ? ")" : $":{item.StartLine})");
            if (!string.IsNullOrWhiteSpace(item.Relation))
                prompt.Append(" [").Append(item.Relation).Append(']');
            prompt.AppendLine();
        }
        if (pack.Paths.Count > 0)
        {
            prompt.AppendLine("Paths:");
            foreach (var path in pack.Paths.Take(12))
                prompt.AppendLine($"- {path.Kind}: {string.Join(" → ", path.NodeIds)}{(path.Truncated ? " (truncated)" : string.Empty)}");
        }
        if (pack.CapabilityGaps.Count > 0)
        {
            prompt.AppendLine("Known gaps:");
            foreach (var gap in pack.CapabilityGaps)
                prompt.AppendLine($"- {gap}");
        }
        return prompt.ToString();
    }

    private async Task<EvidenceItem> ToEvidenceAsync(
        string rootPath,
        ScoredGraphNode hit,
        int relevance,
        CancellationToken ct)
    {
        var node = hit.Node;
        var strongest = StrongestEvidence(node.Evidence);
        return new(
            $"node:{node.Id}",
            node.Kind.ToString(),
            $"{node.Role}：{node.Name}",
            ToEvidenceConfidence(strongest?.Confidence ?? V3GraphConfidence.Inferred),
            strongest is null ? "GraphRAGV3" : EvidenceSource(strongest.Source),
            node.FilePath,
            node.StartLine,
            node.EndLine,
            Symbol: node.Id,
            Excerpt: await ReadExcerptAsync(
                rootPath, node.FilePath, node.StartLine, node.EndLine, ct),
            Reason: strongest?.Reason ?? "由 GraphRAG V3 關聯檢索取得。",
            Relevance: relevance);
    }

    private async Task<string?> ReadExcerptAsync(
        string rootPath,
        string? relativePath,
        int? startLine,
        int? endLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            var prefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                return null;

            var first = Math.Max(1, startLine ?? 1);
            var last = Math.Max(first, Math.Min(endLine ?? first + 12, first + 40));
            await using var stream = new FileStream(
                candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var excerpt = new StringBuilder(1200);
            for (var lineNumber = 1; lineNumber <= last; lineNumber++)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (lineNumber < first) continue;
                if (excerpt.Length > 0) excerpt.AppendLine();
                var remaining = 1200 - excerpt.Length;
                if (remaining <= 0) break;
                excerpt.Append(line.AsSpan(0, Math.Min(line.Length, remaining)));
            }
            return excerpt.Length == 0 ? null : redactor.Redact(excerpt.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private string RedactedBounded(string value)
    {
        var bounded = value.Length <= 1200 ? value : value[..1200];
        return redactor.Redact(bounded);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    private static EvidenceConfidence ToEvidenceConfidence(V3GraphConfidence confidence) => confidence switch
    {
        V3GraphConfidence.Exact => EvidenceConfidence.Exact,
        V3GraphConfidence.Resolved => EvidenceConfidence.Resolved,
        V3GraphConfidence.Heuristic => EvidenceConfidence.Heuristic,
        V3GraphConfidence.Inferred => EvidenceConfidence.Inferred,
        _ => EvidenceConfidence.Inferred,
    };

    private static string EvidenceSource(GraphEvidenceSource source) =>
        $"GraphRAGV3:{source}";

    private static int Score(ScoredGraphNode hit, string query) =>
        hit.Node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            ? 100
            : Math.Clamp((int)Math.Round(hit.Score * 10), 50, 95);

    private static GraphEvidence? StrongestEvidence(
        IReadOnlyList<GraphEvidence> evidence) =>
        evidence.OrderBy(item => ConfidenceRank(item.Confidence)).FirstOrDefault();

    private static V3GraphConfidence StrongestConfidence(
        IReadOnlyList<GraphEvidence> evidence) =>
        StrongestEvidence(evidence)?.Confidence ?? V3GraphConfidence.Inferred;

    private static int ConfidenceRank(V3GraphConfidence confidence) => confidence switch
    {
        V3GraphConfidence.Exact => 0,
        V3GraphConfidence.Resolved => 1,
        V3GraphConfidence.Heuristic => 2,
        _ => 3,
    };

    private static IReadOnlyList<string> ExtractSearchTerms(string text) =>
        text.Split([' ', '\t', '\r', '\n', '，', '。', '？', '?', '、'], StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length >= 3)
            .OrderByDescending(term => term.Any(char.IsUpper) || term.Contains('.'))
            .Take(3)
            .ToList();

    private static IndexFreshness ToFreshness(ProjectIndexStatus status) => status switch
    {
        ProjectIndexStatus.Indexed => IndexFreshness.Fresh,
        ProjectIndexStatus.PendingChanges => IndexFreshness.PendingChanges,
        ProjectIndexStatus.Indexing => IndexFreshness.Indexing,
        ProjectIndexStatus.Partial => IndexFreshness.Partial,
        ProjectIndexStatus.Failed => IndexFreshness.Failed,
        ProjectIndexStatus.Stale => IndexFreshness.Stale,
        _ => IndexFreshness.Stale,
    };

    private static string? ManifestVersion(ProjectEntity project) =>
        project.IndexManifestVersion;
}
