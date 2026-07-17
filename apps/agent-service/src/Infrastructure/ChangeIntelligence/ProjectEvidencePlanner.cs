using System.Text;
using System.Security.Cryptography;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.ChangeIntelligence.DataIntelligence;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>
/// 將既有 Code Graph 查詢收斂成有界、可追溯的 Evidence Pack。
/// 這是 P2 的 deterministic data plane；LLM 只能根據回傳 evidence 作解釋，
/// 不負責猜測哪一個圖譜工具應被使用。
/// </summary>
public sealed class ProjectEvidencePlanner(
    ICodeGraphStore graph,
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
            var hits = await graph.SearchAsync(project.Id, query, 8, ct);
            foreach (var hit in hits)
            {
                if (!seenNodes.Add(hit.Key))
                    continue;

                evidence.Add(await ToEvidenceAsync(project.RootPath, hit, relevance: Score(hit, query), ct));
                var neighborhood = await graph.GetNeighborhoodAsync(project.Id, hit.Key, 1, ct);
                foreach (var neighbor in neighborhood.Neighbors.Take(12))
                {
                    var id = $"node:{neighbor.Key}";
                    evidence.Add(new EvidenceItem(
                        id,
                        neighbor.Kind,
                        $"{neighbor.Direction switch { "in" => "使用／呼叫端", _ => "相依／被呼叫端" }}：{neighbor.Name}",
                        ToEvidenceConfidence(neighbor.Confidence),
                        EvidenceSource(neighbor.SourceKind, neighbor.ExtractorId),
                        neighbor.FilePath,
                        neighbor.StartLine,
                        neighbor.EndLine,
                        Symbol: neighbor.Key,
                        Relation: neighbor.RelationKind,
                        Excerpt: await ReadExcerptAsync(
                            project.RootPath, neighbor.FilePath, neighbor.StartLine, neighbor.EndLine, ct),
                        Reason: neighbor.Reason,
                        Relevance: 50));
                }

                if (brief.Classification.AnalysisMode is ChangeAnalysisMode.ImpactAnalysis or ChangeAnalysisMode.ImplementationPlanning)
                {
                    var chains = await graph.GetReverseCallChainAsync(project.Id, hit.Key, 3, ct);
                    foreach (var chain in chains.Take(20))
                    {
                        var ids = chain.Chain.Select(node => $"node:{node.Key}").ToList();
                        paths.Add(new EvidencePath(
                            "reverse-call",
                            ids,
                            ToEvidenceConfidence(chain.Confidence),
                            chain.Truncated || chains.Count > 20));
                        foreach (var node in chain.Chain)
                        {
                            if (seenNodes.Add(node.Key))
                                evidence.Add(await ToEvidenceAsync(project.RootPath, node, relevance: 80, ct));
                        }
                    }
                }
            }
        }

        var codeEvidenceCount = evidence.Count;
        var dataEvidence = await dataEvidencePlanner.BuildAsync(brief, ct);
        evidence.AddRange(dataEvidence.Items);

        var gaps = new List<string>(dataEvidence.CapabilityGaps);
        if (queries.Count == 0)
            gaps.Add("未提供可用的程式碼目標；目前只能依自然語言建立初步假設。");
        if (codeEvidenceCount == 0)
            gaps.Add("Code Graph 未找到可驗證的相符實體；可能需要重新索引或補充檔案、Symbol、Route 或錯誤 log。");
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
        GraphSearchHit hit,
        int relevance,
        CancellationToken ct) => new(
            $"node:{hit.Key}",
            hit.Kind,
            hit.Signature ?? hit.Name,
            ToEvidenceConfidence(hit.Confidence),
            EvidenceSource(hit.SourceKind, hit.ExtractorId),
            hit.FilePath,
            hit.StartLine,
            hit.EndLine,
            Symbol: hit.Key,
            Excerpt: await ReadExcerptAsync(rootPath, hit.FilePath, hit.StartLine, hit.EndLine, ct),
            Reason: hit.Reason,
            Relevance: relevance);

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

    private static EvidenceConfidence ToEvidenceConfidence(GraphConfidence confidence) => confidence switch
    {
        GraphConfidence.Confirmed => EvidenceConfidence.Confirmed,
        GraphConfidence.Exact => EvidenceConfidence.Exact,
        GraphConfidence.Resolved => EvidenceConfidence.Resolved,
        GraphConfidence.Heuristic => EvidenceConfidence.Heuristic,
        GraphConfidence.Inferred => EvidenceConfidence.Inferred,
        _ => EvidenceConfidence.Inferred,
    };

    private static string EvidenceSource(GraphSourceKind sourceKind, string? extractorId) =>
        string.IsNullOrWhiteSpace(extractorId)
            ? sourceKind.ToString()
            : $"{sourceKind}:{extractorId}";

    private static int Score(GraphSearchHit hit, string query) =>
        hit.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 100 : 70;

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
