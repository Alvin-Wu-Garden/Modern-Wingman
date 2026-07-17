using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>
/// P2 的規則優先 router。它刻意保守：命中衝突時選擇可驗證性較高的 Impact / Bug 模式，
/// 並將所有命中訊號保留給上層診斷，而不是以自然語言猜測使用者意圖。
/// </summary>
public sealed partial class DeterministicChangeIntentClassifier : IChangeIntentClassifier
{
    private static readonly string[] ProjectSignals =
    [
        "bug", "error", "exception", "stack trace", "feature", "requirement", "refactor", "regression",
        "api", "route", "endpoint", "database", "schema", "migration", "測試", "驗證", "回歸",
        "錯誤", "異常", "問題", "需求", "功能", "修改", "新增", "重構", "影響", "風險", "程式", "專案",
    ];

    private static readonly string[] ImpactSignals = ["impact", "blast radius", "affect", "會壞", "影響", "風險", "波及", "牽涉"];
    private static readonly string[] BugSignals = ["bug", "error", "exception", "fail", "crash", "stack trace", "錯誤", "異常", "失敗", "當機", "無法", "不會", "沒有作用"];
    private static readonly string[] NewFeatureSignals = ["new feature", "add ", "implement", "新增", "增加", "開發", "支援", "加入"];
    private static readonly string[] RefactorSignals = ["refactor", "cleanup", "重構", "整理", "抽取", "解耦"];
    private static readonly string[] PlacementSignals = ["where", "在哪", "改哪", "加在哪", "放在哪", "哪裡修改"];
    private static readonly string[] PlanSignals = ["plan", "how to implement", "實作規劃", "怎麼做", "如何做", "拆解", "步驟"];
    private static readonly string[] VerificationSignals = ["test", "verify", "validation", "regression", "測試", "驗證", "回歸", "怎麼測"];

    public ChangeIntentClassification Classify(string request, IReadOnlyList<ChangeTarget>? suppliedTargets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        var normalized = request.Trim();
        var signals = new List<string>();
        var targets = suppliedTargets ?? [];

        var hasImpact = Matches(normalized, ImpactSignals, signals, "impact");
        var hasBug = Matches(normalized, BugSignals, signals, "bug");
        var hasFeature = Matches(normalized, NewFeatureSignals, signals, "feature");
        var hasRefactor = Matches(normalized, RefactorSignals, signals, "refactor");
        var hasPlacement = Matches(normalized, PlacementSignals, signals, "placement");
        var hasPlan = Matches(normalized, PlanSignals, signals, "plan");
        var hasVerification = Matches(normalized, VerificationSignals, signals, "verification");
        var explicitTarget = targets.Any(target => target.Kind != ChangeTargetKind.NaturalLanguage)
            || FilePathRegex().IsMatch(normalized)
            || RouteRegex().IsMatch(normalized)
            || ErrorLogRegex().IsMatch(normalized);

        if (explicitTarget)
            signals.Add("explicit-target");

        var kind = hasBug ? ChangeKind.Bug
            : hasFeature ? ChangeKind.NewFeature
            : hasRefactor ? ChangeKind.Refactor
            : hasImpact ? ChangeKind.RiskAssessment
            : ChangeKind.Unknown;

        var mode = hasImpact || targets.Any(t => t.Kind is ChangeTargetKind.GitDiff or ChangeTargetKind.Symbol) ? ChangeAnalysisMode.ImpactAnalysis
            : hasBug ? ChangeAnalysisMode.ProblemLocation
            : hasVerification ? ChangeAnalysisMode.VerificationAndRegression
            : hasPlan ? ChangeAnalysisMode.ImplementationPlanning
            : hasPlacement || hasFeature ? ChangeAnalysisMode.ChangePlacement
            : ChangeAnalysisMode.Unknown;

        var projectScoped = explicitTarget || ProjectSignals.Any(s => Contains(normalized, s));
        var confidence = explicitTarget && mode != ChangeAnalysisMode.Unknown
            ? ClassificationConfidence.High
            : mode != ChangeAnalysisMode.Unknown || projectScoped
                ? ClassificationConfidence.Medium
                : ClassificationConfidence.Low;

        return new ChangeIntentClassification(kind, mode, confidence, signals, projectScoped);
    }

    private static bool Matches(string value, IEnumerable<string> candidates, ICollection<string> signals, string category)
    {
        var match = candidates.FirstOrDefault(candidate => Contains(value, candidate));
        if (match is null)
            return false;

        signals.Add($"{category}:{match}");
        return true;
    }

    private static bool Contains(string value, string candidate) =>
        value.Contains(candidate, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<!\w)(?:[\w.-]+[\\/])+[\w.-]+\.(?:cs|java|ts|tsx|js|jsx|json|yml|yaml|sql|xml|properties)(?!\w)", RegexOptions.IgnoreCase)]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"\b(?:GET|POST|PUT|PATCH|DELETE)\s+/[^\s，。？?]+", RegexOptions.IgnoreCase)]
    private static partial Regex RouteRegex();

    [GeneratedRegex(@"(?:Exception|Error|at\s+[\w.$]+\(|\b(?:ORA|SQLITE|HTTP)\s*[-:]?\s*\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLogRegex();
}
