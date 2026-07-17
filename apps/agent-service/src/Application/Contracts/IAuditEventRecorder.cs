using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IAuditEventRecorder
{
    Task RecordAsync(AuditEventWrite evt, CancellationToken ct = default);
}
