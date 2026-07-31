using AgentService.Application.Atlassian;
using AgentService.Infrastructure.Atlassian;

namespace AgentService.UnitTests;

public sealed class JiraFeatureIdentifierExtractorTests
{
    private readonly JiraFeatureIdentifierExtractor _extractor = new();

    [Fact]
    public void Extract_CodeAndName_FromSummary()
    {
        var issue = CreateIssue(summary: "按下放行時 4204-放行作業 無法送單");

        var result = _extractor.Extract(issue);

        var item = Assert.Single(result);
        Assert.Equal("4204", item.FeatureCode);
        Assert.Equal("放行作業", item.FeatureName);
        Assert.Equal(JiraFeatureSourceType.Summary, item.SourceType);
        Assert.True(item.Confidence >= 0.90);
        Assert.True(item.IsConfirmed);
    }

    [Fact]
    public void Extract_CodeOnly_WhenFeatureKeywordAppearsInContext()
    {
        var issue = CreateIssue(description: "交易流程在功能 4210 會卡住，請協助排查");

        var result = _extractor.Extract(issue);

        var item = Assert.Single(result);
        Assert.Equal("4210", item.FeatureCode);
        Assert.Null(item.FeatureName);
        Assert.Equal(JiraFeatureSourceType.Description, item.SourceType);
    }

    [Fact]
    public void Extract_Excludes_JiraKey_Pattern()
    {
        var issue = CreateIssue(summary: "JIRA-4204 處理完成，但不是功能代號");

        var result = _extractor.Extract(issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_Excludes_YearLikeCode()
    {
        var issue = CreateIssue(summary: "2024-12-31 問題重現，請看說明");

        var result = _extractor.Extract(issue);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_MergesOccurrences_AndRaisesOccurrenceCount()
    {
        var issue = CreateIssue(
            summary: "4204-放行作業 出現錯誤",
            description: "使用 4204-放行作業 時可重現",
            comments: ["4204-放行作業 在 UAT 也有"]);

        var result = _extractor.Extract(issue);

        var item = Assert.Single(result);
        Assert.Equal("4204", item.FeatureCode);
        Assert.Equal("放行作業", item.FeatureName);
        Assert.Equal(3, item.OccurrenceCount);
        Assert.True(item.IsConfirmed);
        Assert.Contains("4204-放行作業", item.Evidence);
    }

    [Fact]
    public void Extract_ReadsFromClassifiedField_AndMarksSourceReference()
    {
        var issue = CreateIssue(
            classifiedFields: new Dictionary<string, string>
            {
                ["問題分析"] = "問題集中在 4501-付款維護",
            });

        var result = _extractor.Extract(issue);

        var item = Assert.Single(result);
        Assert.Equal("4501", item.FeatureCode);
        Assert.Equal("付款維護", item.FeatureName);
        Assert.Equal("field:問題分析", item.SourceReference);
        Assert.Equal(JiraFeatureSourceType.ClassifiedField, item.SourceType);
    }

    [Fact]
    public void Extract_IncludesLinkedIssueSummary()
    {
        var issue = CreateIssue(linkedIssueSummaries: ["先修正 4330-申請作業 的資料驗證"]);

        var result = _extractor.Extract(issue);

        var item = Assert.Single(result);
        Assert.Equal("4330", item.FeatureCode);
        Assert.Equal("申請作業", item.FeatureName);
        Assert.Equal(JiraFeatureSourceType.LinkedIssue, item.SourceType);
    }

    [Fact]
    public void Extract_AttachmentOnly_HasLowerConfidence_AndUnconfirmed()
    {
        var issue = CreateIssue(attachments: ["功能4209-查詢作業-錯誤截圖.png"]);

        var result = _extractor.Extract(issue);

        var item = Assert.Single(result);
        Assert.Equal("4209", item.FeatureCode);
        Assert.True(item.Confidence < 0.70);
        Assert.False(item.IsConfirmed);
    }

    [Fact]
    public void Extract_PrefersHigherPrioritySource_WhenMerged()
    {
        var issue = CreateIssue(
            summary: "4204-放行作業 異常",
            components: ["4204-放行作業"]);

        var result = _extractor.Extract(issue);

        var item = Assert.Single(result);
        Assert.Equal(JiraFeatureSourceType.Summary, item.SourceType);
        Assert.Equal("summary", item.SourceReference);
    }

    private static NormalizedJiraIssue CreateIssue(
        string summary = "一般問題",
        string? description = null,
        IReadOnlyDictionary<string, string>? classifiedFields = null,
        IReadOnlyList<string>? components = null,
        IReadOnlyList<string>? linkedIssueSummaries = null,
        IReadOnlyList<string>? comments = null,
        IReadOnlyList<string>? attachments = null)
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
            DescriptionMarkdown = description,
            ClassifiedFields = classifiedFields ?? new Dictionary<string, string>(),
            Components = components ?? [],
            LinkedIssues = (linkedIssueSummaries ?? [])
                .Select((s, i) => new JiraLinkedIssue
                {
                    Key = $"WING-{i + 2}",
                    Summary = s,
                    LinkType = "relates",
                })
                .ToList(),
            Comments = (comments ?? [])
                .Select((body, i) => new JiraCommentItem
                {
                    Id = $"c{i + 1}",
                    AuthorDisplayName = "tester",
                    Body = body,
                    Created = "2026-01-01",
                })
                .ToList(),
            Attachments = (attachments ?? [])
                .Select(name => new JiraAttachmentInfo
                {
                    Filename = name,
                    MimeType = "image/png",
                    Size = 1024,
                })
                .ToList(),
        };
    }
}