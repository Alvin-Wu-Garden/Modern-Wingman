using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// SQL fallback 刻意分離 query plan 與 binding 值。Binding source 只能在本次呼叫存活，
/// 不得記錄、序列化或傳回給 Evidence Pack。
/// </summary>
public interface IRuntimeQueryBindingSource
{
    bool Contains(string parameterName);
    object? GetValue(string parameterName);
}

public interface IReadOnlyDatabaseQueryPlanValidator
{
    DatabaseQueryPlanValidationResult Validate(DatabaseReadOnlyQueryPlan plan);
}

/// <summary>避免結構化 Runtime lookup 被當成無範圍資料 dump 使用。</summary>
public interface IDatabaseRuntimeEvidenceRequestValidator
{
    DatabaseRuntimeRequestValidationResult Validate(DatabaseConfigurationLookup lookup);
    DatabaseRuntimeRequestValidationResult Validate(DatabaseSchemaInspectionRequest request);
}
