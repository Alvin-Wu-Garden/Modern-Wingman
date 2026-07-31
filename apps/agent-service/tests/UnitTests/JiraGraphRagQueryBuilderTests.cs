using AgentService.Application.Atlassian;

namespace AgentService.UnitTests;

public sealed class JiraGraphRagQueryBuilderTests
{
    private readonly JiraGraphRagQueryBuilder _builder = new();

    [Fact]
    public void Build_IncludesConfirmedFeatureCodesAndNamesInLocalQuery()
    {
        var issue = CreateIssue("4204 放行失敗，需追查流程");
        var ids = new List<JiraFeatureIdentifier>
        {
            new("4204", "放行作業", JiraFeatureSourceType.Summary, "summary", 0.95, "4204-放行作業", 2, true),
        };

        var result = _builder.Build(issue, ids);

        Assert.Contains("4204", result.LocalQuery);
        Assert.Contains("放行作業", result.LocalQuery);
        Assert.Contains("ControllerAction Handles RoutesTo", result.LocalQuery);
    }

    [Fact]
    public void Build_FiltersOutLowConfidenceUnconfirmedIdentifier()
    {
        var issue = CreateIssue("請分析查詢異常");
        var ids = new List<JiraFeatureIdentifier>
        {
            new("4999", "測試", JiraFeatureSourceType.Attachment, "attachment", 0.41, "4999-測試", 1, false),
        };

        var result = _builder.Build(issue, ids);

        Assert.DoesNotContain("4999", result.LocalQuery);
        Assert.Empty(result.FeatureCodes);
    }

    [Fact]
    public void Build_DeduplicatesFeatureCodesAndNames()
    {
        var issue = CreateIssue("4204 問題");
        var ids = new List<JiraFeatureIdentifier>
        {
            new("4204", "放行作業", JiraFeatureSourceType.Summary, "summary", 0.95, "4204-放行作業", 3, true),
            new("4204", "放行作業", JiraFeatureSourceType.Comment, "comment", 0.90, "4204-放行作業", 2, true),
        };

        var result = _builder.Build(issue, ids);

        Assert.Single(result.FeatureCodes);
        Assert.Single(result.FeatureNames);
    }

    [Fact]
    public void Build_GlobalQuery_ContainsImpactTerms()
    {
        var issue = CreateIssue("轉檔後報表錯誤");
        var result = _builder.Build(issue, []);

        Assert.Contains("impact root-cause dependency risk", result.GlobalQuery);
    }

    [Fact]
    public void Build_RespectsLengthLimits()
    {
        var issue = CreateIssue(string.Join(' ', Enumerable.Repeat("超長描述token", 80)));
        var ids = Enumerable.Range(0, 12)
            .Select(i => new JiraFeatureIdentifier(
                $"{4200 + i}",
                $"功能{i}",
                JiraFeatureSourceType.Summary,
                "summary",
                0.92,
                $"{4200 + i}-功能{i}",
                2,
                true))
            .ToList();

        var result = _builder.Build(issue, ids);

        Assert.True(result.LocalQuery.Length <= 220);
        Assert.True(result.GlobalQuery.Length <= 180);
    }

    private static NormalizedJiraIssue CreateIssue(string summary)
    {
        return new NormalizedJiraIssue
        {
            Preview = new JiraIssuePreview
            {
                Key = "WING-1",
                Summary = summary,
                Status = "Open",
                IssueType = "Bug",
                ProjectKey = "WING",
                ProjectName = "Wingman",
            },
        };
    }
}