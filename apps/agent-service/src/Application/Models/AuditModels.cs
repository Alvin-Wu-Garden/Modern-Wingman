namespace AgentService.Application.Models;

public sealed record AuditQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? EventType = null,
    string? TargetType = null,
    string? TargetId = null,
    string? Result = null,
    string? TraceId = null,
    int Offset = 0,
    int Limit = 100);

public sealed record AuditEventDto(
    string Id, string? TraceId, string ActorType, string? ActorId,
    string EventType, string TargetType, string? TargetId, string Action,
    string Result, string? MachineName, string? BeforeHash, string? AfterHash,
    string? DetailsJson, DateTimeOffset CreatedAt);

public sealed record AuditPage(IReadOnlyList<AuditEventDto> Items, int Total, int Offset, int Limit);
public sealed record ToolCallAuditQuery(DateTimeOffset? From=null,DateTimeOffset? To=null,string? ProjectId=null,string? RunId=null,string? Provider=null,string? Tool=null,string? Status=null,int Offset=0,int Limit=100);
public sealed record ToolCallAuditDto(string Id,string TraceId,string? ProjectId,string? RunId,string Provider,string ToolName,string ToolType,string Status,DateTimeOffset StartedAt,long? DurationMs,bool ApprovalRequired,string? ApprovalResult,string? Error);
public sealed record ToolCallAuditPage(IReadOnlyList<ToolCallAuditDto> Items,int Total,int Offset,int Limit);
public sealed record AuditFilterOption(string Value,string Label,string? Group=null);
public sealed record AuditFacets(
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> TargetTypes,
    IReadOnlyList<AuditFilterOption> Targets,
    IReadOnlyList<string> Results,
    IReadOnlyList<string> TraceIds);
public sealed record ToolCallAuditFacets(
    IReadOnlyList<AuditFilterOption> Projects,
    IReadOnlyList<AuditFilterOption> Runs,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Statuses);
