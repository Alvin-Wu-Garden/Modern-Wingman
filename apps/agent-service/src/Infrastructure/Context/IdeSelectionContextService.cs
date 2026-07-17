using AgentService.Application.Contracts;
using AgentService.Infrastructure.Tools;

namespace AgentService.Infrastructure.Context;

public sealed class IdeSelectionContextService : IIdeSelectionContextService
{
    public async Task<IdeSelectionContext> ReadAsync(
        string workspacePath,
        string relativePath,
        int startLine,
        int endLine,
        CancellationToken ct = default)
    {
        if (startLine < 1 || endLine < startLine || endLine - startLine + 1 > 500)
            throw new ArgumentOutOfRangeException(nameof(startLine), "IDE selection must contain 1-500 lines.");
        var path = WorkspacePathGuard.ResolveReadable(workspacePath, relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Selected source file was not found.", relativePath);
        var lines = await File.ReadAllLinesAsync(path, ct);
        if (startLine > lines.Length) throw new ArgumentOutOfRangeException(nameof(startLine), "Selection starts after the end of the file.");
        var actualEnd = Math.Min(endLine, lines.Length);
        var content = string.Join(Environment.NewLine, lines[(startLine - 1)..actualEnd]);
        if (content.Length > 100_000) throw new InvalidOperationException("IDE selection exceeds 100,000 characters.");
        var normalized = Path.GetRelativePath(Path.GetFullPath(workspacePath), path).Replace('\\', '/');
        return new(
            normalized,
            startLine,
            actualEnd,
            content,
            UntrustedContent.Wrap($"ide-selection:{normalized}:{startLine}-{actualEnd}", content));
    }
}
