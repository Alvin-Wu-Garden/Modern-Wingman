namespace AgentService.Application.Contracts;
public sealed record ContextSource(string Kind,string Path,int Characters);
public sealed record AssembledContext(string Prompt,IReadOnlyList<ContextSource> Sources,int EstimatedTokens,bool Truncated);
public interface IContextAssembler{Task<AssembledContext> AssembleAsync(string message,string workspacePath,CancellationToken ct=default,string? runId=null);}
