using AgentService.Application.Models;
namespace AgentService.Application.Contracts;
public interface IAuditQueryService
{
    Task<AuditPage> QueryAsync(AuditQuery query,CancellationToken ct=default);
    Task<string> ExportCsvAsync(AuditQuery query,CancellationToken ct=default);
    Task<ToolCallAuditPage> QueryToolCallsAsync(ToolCallAuditQuery query,CancellationToken ct=default);
    Task<string> ExportToolCallsCsvAsync(ToolCallAuditQuery query,CancellationToken ct=default);
    Task<AuditFacets> GetFacetsAsync(CancellationToken ct=default);
    Task<ToolCallAuditFacets> GetToolCallFacetsAsync(CancellationToken ct=default);
}

public interface ISensitiveDataRedactor { string Redact(string value); }
public interface IAuditMaintenanceService { Task<int> DeleteExpiredAsync(CancellationToken ct=default); }
