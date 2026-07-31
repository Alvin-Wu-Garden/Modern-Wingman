namespace AgentService.Application.Atlassian;

/// <summary>
/// JIRA 內容中的功能識別候選來源。
/// </summary>
public enum JiraFeatureSourceType
{
    Summary,
    Description,
    ClassifiedField,
    Component,
    LinkedIssue,
    Comment,
    Attachment,
}

/// <summary>
/// 從 JIRA 擷取到的功能代號與功能名稱候選。
/// </summary>
public sealed record JiraFeatureIdentifier(
    string? FeatureCode,
    string? FeatureName,
    JiraFeatureSourceType SourceType,
    string SourceReference,
    double Confidence,
    string Evidence,
    int OccurrenceCount,
    bool IsConfirmed)
{
    public string CombinedName =>
        !string.IsNullOrWhiteSpace(FeatureCode) &&
        !string.IsNullOrWhiteSpace(FeatureName)
            ? $"{FeatureCode}-{FeatureName}"
            : FeatureCode ?? FeatureName ?? string.Empty;
}