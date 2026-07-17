using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.ChangeIntelligence;

namespace AgentService.UnitTests;

public sealed class DeterministicChangeIntentClassifierTests
{
    private readonly DeterministicChangeIntentClassifier _classifier = new();

    [Fact]
    public void Classify_ExplicitSymbolImpactRequest_PrefersImpactAnalysis()
    {
        var result = _classifier.Classify(
            "修改 MarketplacePluginService.SaveConfigurationAsync 會影響什麼？",
            [new ChangeTarget(ChangeTargetKind.Symbol, "MarketplacePluginService.SaveConfigurationAsync")]);

        Assert.Equal(ChangeKind.RiskAssessment, result.ChangeKind);
        Assert.Equal(ChangeAnalysisMode.ImpactAnalysis, result.AnalysisMode);
        Assert.Equal(ClassificationConfidence.High, result.Confidence);
        Assert.Contains("explicit-target", result.Signals);
    }

    [Fact]
    public void Classify_BugReport_RoutesToProblemLocation()
    {
        var result = _classifier.Classify("訂單建立後畫面顯示錯誤，預期應該成功回傳。");

        Assert.Equal(ChangeKind.Bug, result.ChangeKind);
        Assert.Equal(ChangeAnalysisMode.ProblemLocation, result.AnalysisMode);
        Assert.True(result.IsProjectScoped);
    }

    [Fact]
    public void Classify_UnrelatedQuestion_IsNotProjectScoped()
    {
        var result = _classifier.Classify("今天天氣如何？");

        Assert.Equal(ChangeAnalysisMode.Unknown, result.AnalysisMode);
        Assert.False(result.IsProjectScoped);
        Assert.Equal(ClassificationConfidence.Low, result.Confidence);
    }
}

public sealed class ChangeBriefBuilderTests
{
    private readonly ChangeBriefBuilder _builder = new(new DeterministicChangeIntentClassifier());

    [Fact]
    public void Build_ExtractsFileRouteAndErrorLogTargets()
    {
        var brief = _builder.Build("project-1", "POST /api/orders 在 src/Orders/OrderController.cs 發生 InvalidOperationException");

        Assert.Contains(brief.Targets, target => target.Kind == ChangeTargetKind.File && target.Value.EndsWith("OrderController.cs"));
        Assert.Contains(brief.Targets, target => target.Kind == ChangeTargetKind.Route && target.Value == "POST /api/orders");
        Assert.Contains(brief.Targets, target => target.Kind == ChangeTargetKind.ErrorLog);
        Assert.Equal(ChangeKind.Bug, brief.Classification.ChangeKind);
    }

    [Fact]
    public void Build_NaturalLanguageOnly_RecordsTargetingUnknown()
    {
        var brief = _builder.Build("project-1", "新增會員等級功能");

        Assert.Single(brief.Targets);
        Assert.Equal(ChangeTargetKind.NaturalLanguage, brief.Targets[0].Kind);
        Assert.Contains(brief.Unknowns, unknown => unknown.Contains("尚未提供可定位"));
    }
}

public sealed class DeterministicClarificationPlannerTests
{
    private readonly ChangeBriefBuilder _builder = new(new DeterministicChangeIntentClassifier());
    private readonly DeterministicClarificationPlanner _planner = new();

    [Fact]
    public void Plan_NewFeature_ProducesDecisionFocusedQuestionsWithoutExceedingLimit()
    {
        var brief = _builder.Build("project-1", "新增會員等級功能");
        var questions = _planner.Plan(brief);

        Assert.InRange(questions.Count, 1, 10);
        Assert.Contains(questions, question => question.Category == "authorization");
        Assert.Contains(questions, question => question.Category == "data-lifecycle");
        Assert.All(questions, question => Assert.False(string.IsNullOrWhiteSpace(question.DecisionImpact)));
    }

    [Fact]
    public void Plan_RejectsOutOfRangeLimit()
    {
        var brief = _builder.Build("project-1", "修正錯誤");

        Assert.Throws<ArgumentOutOfRangeException>(() => _planner.Plan(brief, 11));
    }
}

public sealed class BoundedEvidencePackBuilderTests
{
    private readonly ChangeBriefBuilder _briefBuilder = new(new DeterministicChangeIntentClassifier());
    private readonly BoundedEvidencePackBuilder _builder = new();

    [Fact]
    public void Build_DeduplicatesOrdersAndTruncatesEvidence()
    {
        var brief = _briefBuilder.Build("project-1", "修改 OrderService 會影響什麼？");
        var pack = _builder.Build(new EvidencePackRequest(
            brief,
            [
                new EvidenceItem("exact", "symbol", "OrderService", EvidenceConfidence.Exact, "compiler", "Order.cs", 10, Relevance: 10, Excerpt: new string('a', 20)),
                new EvidenceItem("exact", "symbol", "weaker duplicate", EvidenceConfidence.Heuristic, "heuristic", "Other.cs", 1, Relevance: 1),
                new EvidenceItem("inferred", "candidate", "Order worker", EvidenceConfidence.Inferred, "framework", "Worker.cs", 3, Relevance: 5),
            ],
            [new EvidencePath("call-chain", ["exact", "inferred"], EvidenceConfidence.Resolved)],
            IndexFreshness.PendingChanges,
            "manifest-1",
            MaxItems: 1,
            MaxExcerptCharacters: 8,
            CapabilityGaps: ["Spring extractor unavailable", "Spring extractor unavailable"]));

        Assert.Single(pack.Items);
        Assert.Equal("exact", pack.Items[0].Id);
        Assert.Equal("aaaaaaaa…", pack.Items[0].Excerpt);
        Assert.True(pack.Truncated);
        Assert.Equal(IndexFreshness.PendingChanges, pack.Freshness);
        Assert.Single(pack.CapabilityGaps);
        Assert.Single(pack.Paths);
        Assert.True(pack.Paths[0].Truncated);
        Assert.Equal(["exact"], pack.Paths[0].NodeIds);
    }
}

public sealed class ChangeAnalysisSessionServiceTests
{
    [Fact]
    public async Task StartOrContinue_PersistsAnswersAndConvergesBrief()
    {
        var store = new MemorySessionStore();
        var briefBuilder = new ChangeBriefBuilder(new DeterministicChangeIntentClassifier());
        var service = new ChangeAnalysisSessionService(store, briefBuilder, new DeterministicClarificationPlanner());
        var target = new ChangeTarget(ChangeTargetKind.File, "src/MemberService.cs", "test");

        var started = await service.StartOrContinueAsync("project-1", "新增會員等級功能", [target], null, null);
        Assert.Equal(ChangeAnalysisSessionStatus.AwaitingClarification, started.Status);

        var continued = await service.StartOrContinueAsync(
            "project-1",
            "補充澄清資訊",
            null,
            started.Id,
            [
                new("authorization", "管理員可維護，會員只能讀取。"),
                new("contract", "新增回應欄位但保持舊欄位相容。"),
                new("data-lifecycle", "既有會員預設為一般等級。"),
                new("acceptance", "管理員更新後會員立即看到新等級。"),
            ]);

        Assert.Equal(ChangeAnalysisSessionStatus.ReadyForAnalysis, continued.Status);
        Assert.Equal(started.Id, continued.Id);
        Assert.Contains(continued.Brief.Constraints, item => item.Contains("既有會員預設"));
        Assert.Contains(continued.Brief.CandidateAreas, item => item == "src/MemberService.cs");
        Assert.DoesNotContain(continued.PendingQuestions, question => question.IsBlocking);
    }

    [Fact]
    public async Task StartOrContinue_RejectsSessionFromAnotherProject()
    {
        var store = new MemorySessionStore();
        var briefBuilder = new ChangeBriefBuilder(new DeterministicChangeIntentClassifier());
        var service = new ChangeAnalysisSessionService(store, briefBuilder, new DeterministicClarificationPlanner());
        var started = await service.StartOrContinueAsync("project-1", "修正錯誤", null, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartOrContinueAsync(
            "project-2", "補充", null, started.Id, null));
    }

    [Fact]
    public async Task StartOrContinue_AcceptsAllFiveStructuredTargetKinds()
    {
        var store = new MemorySessionStore();
        var briefBuilder = new ChangeBriefBuilder(new DeterministicChangeIntentClassifier());
        var service = new ChangeAnalysisSessionService(store, briefBuilder, new DeterministicClarificationPlanner());
        var targets = new[]
        {
            new ChangeTarget(ChangeTargetKind.File, "src/Orders.cs"),
            new ChangeTarget(ChangeTargetKind.Symbol, "Orders.Create"),
            new ChangeTarget(ChangeTargetKind.Route, "POST /api/orders"),
            new ChangeTarget(ChangeTargetKind.GitDiff, "+ return CreateOrder();"),
            new ChangeTarget(ChangeTargetKind.ErrorLog, "InvalidOperationException at Orders.Create()"),
        };

        var session = await service.StartOrContinueAsync("project-1", "分析修改影響", targets, null, null);

        foreach (var kind in new[] { ChangeTargetKind.File, ChangeTargetKind.Symbol, ChangeTargetKind.Route, ChangeTargetKind.GitDiff, ChangeTargetKind.ErrorLog })
            Assert.Contains(session.Brief.Targets, target => target.Kind == kind);
    }

    [Fact]
    public async Task StartOrContinue_AllowsIncrementalClarificationTurns()
    {
        var store = new MemorySessionStore();
        var briefBuilder = new ChangeBriefBuilder(new DeterministicChangeIntentClassifier());
        var service = new ChangeAnalysisSessionService(store, briefBuilder, new DeterministicClarificationPlanner());
        var started = await service.StartOrContinueAsync("project-1", "新增會員等級功能", [new(ChangeTargetKind.File, "Member.cs")], null, null);

        var partial = await service.StartOrContinueAsync(
            "project-1", "第一輪回答", null, started.Id, [new("authorization", "管理員可修改")]);

        Assert.Equal(ChangeAnalysisSessionStatus.AwaitingClarification, partial.Status);
        Assert.DoesNotContain(partial.PendingQuestions, question => question.Category == "authorization");
        Assert.Contains(partial.PendingQuestions, question => question.IsBlocking);
    }

    private sealed class MemorySessionStore : IChangeAnalysisSessionStore
    {
        private readonly Dictionary<string, ChangeAnalysisSession> _sessions = [];
        public Task<ChangeAnalysisSession?> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(_sessions.GetValueOrDefault(sessionId));
        public Task SaveAsync(ChangeAnalysisSession session, CancellationToken ct = default)
        {
            _sessions[session.Id] = session;
            return Task.CompletedTask;
        }
    }
}

public sealed class ChangeImplementationPlanBuilderTests
{
    [Fact]
    public void Build_ProducesTraceableStepsRisksTestsAndAcceptance()
    {
        var brief = new ChangeBriefBuilder(new DeterministicChangeIntentClassifier())
            .Build("project-1", "修改 OrderService.CalculateTotal 會影響什麼？", [new(ChangeTargetKind.Symbol, "OrderService.CalculateTotal")]);
        var session = new ChangeAnalysisSession(
            "session-1", "project-1", brief, new Dictionary<string, string>(), [],
            ChangeAnalysisSessionStatus.ReadyForAnalysis, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var pack = new EvidencePack(
            brief,
            [new EvidenceItem("node:1", "Method", "CalculateTotal", EvidenceConfidence.Exact, "compiler", "Orders/OrderService.cs", 42, Symbol: "OrderService.CalculateTotal")],
            [new EvidencePath("reverse-call-chain", ["node:1"], EvidenceConfidence.Resolved)],
            IndexFreshness.Fresh,
            "manifest-1",
            [],
            false);

        var plan = new ChangeImplementationPlanBuilder().Build(session, pack);

        Assert.Equal(ChangePlanStatus.Ready, plan.Status);
        Assert.Single(plan.ModificationSteps);
        Assert.Contains("OrderService.cs:42", plan.ModificationSteps[0].Target);
        Assert.Single(plan.ImpactAreas);
        Assert.NotEmpty(plan.Tests);
        Assert.NotEmpty(plan.AcceptanceCriteria);
        Assert.Equal("manifest-1", plan.ManifestVersion);
    }
}
