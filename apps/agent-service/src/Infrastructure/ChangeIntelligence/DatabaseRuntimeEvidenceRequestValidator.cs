using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.ChangeIntelligence;

/// <summary>在呼叫 Database Runtime Plugin 前限制結構化 lookup 的選擇條件與結果範圍。</summary>
public sealed class DatabaseRuntimeEvidenceRequestValidator : IDatabaseRuntimeEvidenceRequestValidator
{
    private const int MaximumConfigurationResults = 100;
    private const int MaximumSchemaResults = 500;
    private const int MaximumSelectorLength = 256;

    public DatabaseRuntimeRequestValidationResult Validate(DatabaseConfigurationLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var errors = new List<string>();
        if (!lookup.HasSelector)
            errors.Add("設定查詢至少需要 key、namespace、feature，或 table 與 column selector。");
        if (lookup.MaxResults is < 1 or > MaximumConfigurationResults)
            errors.Add($"設定查詢 MaxResults 必須介於 1 到 {MaximumConfigurationResults}。");

        foreach (var selector in new[] { lookup.Key, lookup.Namespace, lookup.FeatureName, lookup.Table, lookup.Column, lookup.Environment, lookup.TenantScope })
        {
            if (selector is not null && (selector.Length > MaximumSelectorLength || selector.Any(char.IsControl)))
                errors.Add("設定查詢 selector 不可包含控制字元，且長度不可超過 256 字元。");
        }

        return new(errors.Count == 0, errors.Distinct(StringComparer.Ordinal).ToList());
    }

    public DatabaseRuntimeRequestValidationResult Validate(DatabaseSchemaInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        if (request.MaxResults is < 1 or > MaximumSchemaResults)
            errors.Add($"Schema inspection MaxResults 必須介於 1 到 {MaximumSchemaResults}。");
        if (request.Schemas.Count == 0 && request.ObjectNames.Count == 0)
            errors.Add("Schema inspection 至少需要 schema 或 object selector。");
        if (request.Schemas.Concat(request.ObjectNames).Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumSelectorLength || value.Any(char.IsControl)))
            errors.Add("Schema/object selector 不可為空白、包含控制字元或超過 256 字元。");

        return new(errors.Count == 0, errors.Distinct(StringComparer.Ordinal).ToList());
    }
}
