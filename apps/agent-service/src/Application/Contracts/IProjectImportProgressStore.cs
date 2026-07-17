using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IProjectImportProgressStore
{
    ProjectImportProgress Begin(string operationId, string sourceType);
    ProjectImportProgress? Get(string operationId);
    void Report(string operationId, bool isError, string message);
    void Complete(string operationId, string message);
    void Fail(string operationId, string message);
    void Cancel(string operationId);
}
