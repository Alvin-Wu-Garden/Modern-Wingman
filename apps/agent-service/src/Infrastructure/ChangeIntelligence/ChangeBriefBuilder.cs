using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>從訊息擷取明確可定位線索；其餘需求維持 NaturalLanguage，避免過度猜測。</summary>
public sealed partial class ChangeBriefBuilder(IChangeIntentClassifier classifier) : IChangeBriefBuilder
{
    public ChangeBrief Build(string projectId, string request, IReadOnlyList<ChangeTarget>? suppliedTargets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);

        var targets = suppliedTargets?.Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToList() ?? [];
        AddDetectedTargets(request, targets);
        if (targets.Count == 0)
            targets.Add(new ChangeTarget(ChangeTargetKind.NaturalLanguage, request.Trim(), "user-request"));

        var classification = classifier.Classify(request, targets);
        var unknowns = BuildUnknowns(classification, targets, request);
        var symptom = classification.ChangeKind == ChangeKind.Bug ? request.Trim() : null;
        var expectedBehavior = ContainsExpectedBehavior(request) ? request.Trim() : null;
        var candidates = targets
            .Where(t => t.Kind is not ChangeTargetKind.NaturalLanguage and not ChangeTargetKind.ErrorLog)
            .Select(t => t.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ChangeBrief(
            projectId,
            request.Trim(),
            classification,
            targets,
            symptom,
            ExpectedBehavior: expectedBehavior,
            Constraints: [],
            KnownBoundaries: [],
            CandidateAreas: candidates,
            Unknowns: unknowns);
    }

    private static void AddDetectedTargets(string request, ICollection<ChangeTarget> targets)
    {
        foreach (Match match in FilePathRegex().Matches(request))
            AddIfMissing(targets, new(ChangeTargetKind.File, match.Value, "message"));
        foreach (Match match in RouteRegex().Matches(request))
            AddIfMissing(targets, new(ChangeTargetKind.Route, match.Value, "message"));
        if (ErrorLogRegex().IsMatch(request))
            AddIfMissing(targets, new(ChangeTargetKind.ErrorLog, request.Trim(), "message"));

        // Qualified symbols are intentionally restricted to a dot-separated identifier so ordinary prose is not misclassified.
        foreach (Match match in SymbolRegex().Matches(request))
            AddIfMissing(targets, new(ChangeTargetKind.Symbol, match.Value, "message"));
    }

    private static void AddIfMissing(ICollection<ChangeTarget> targets, ChangeTarget target)
    {
        if (!targets.Any(existing => existing.Kind == target.Kind && string.Equals(existing.Value, target.Value, StringComparison.OrdinalIgnoreCase)))
            targets.Add(target);
    }

    private static IReadOnlyList<string> BuildUnknowns(ChangeIntentClassification classification, IReadOnlyList<ChangeTarget> targets, string request)
    {
        var unknowns = new List<string>();
        if (targets.All(t => t.Kind == ChangeTargetKind.NaturalLanguage))
            unknowns.Add("尚未提供可定位的檔案、Symbol、Route、Git diff 或錯誤 log。");
        if (classification.ChangeKind == ChangeKind.Bug && !ContainsExpectedBehavior(request))
            unknowns.Add("尚未說明預期行為與實際行為的差異。");
        if (classification.AnalysisMode == ChangeAnalysisMode.Unknown)
            unknowns.Add("尚未能以規則判定應優先進行定位、影響分析、實作規劃或驗證。");
        return unknowns;
    }

    private static bool ContainsExpectedBehavior(string request) =>
        request.Contains("預期", StringComparison.OrdinalIgnoreCase) ||
        request.Contains("expected", StringComparison.OrdinalIgnoreCase) ||
        request.Contains("應該", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<!\w)(?:[\w.-]+[\\/])+[\w.-]+\.(?:cs|java|ts|tsx|js|jsx|json|yml|yaml|sql|xml|properties)(?!\w)", RegexOptions.IgnoreCase)]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"\b(?:GET|POST|PUT|PATCH|DELETE)\s+/[^\s，。？?]+", RegexOptions.IgnoreCase)]
    private static partial Regex RouteRegex();

    [GeneratedRegex(@"(?:Exception|Error|at\s+[\w.$]+\(|\b(?:ORA|SQLITE|HTTP)\s*[-:]?\s*\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLogRegex();

    [GeneratedRegex(@"\b[A-Za-z_]\w*(?:\.[A-Za-z_]\w*){1,}(?:\([^)]*\))?")]
    private static partial Regex SymbolRegex();
}
