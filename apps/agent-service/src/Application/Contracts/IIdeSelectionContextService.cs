namespace AgentService.Application.Contracts;

public sealed record IdeSelectionContext(
    string RelativePath,
    int StartLine,
    int EndLine,
    string Content,
    string UntrustedContent);

public interface IIdeSelectionContextService
{
    Task<IdeSelectionContext> ReadAsync(
        string workspacePath,
        string relativePath,
        int startLine,
        int endLine,
        CancellationToken ct = default);
}
