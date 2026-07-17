using AgentService.Application.Models;
namespace AgentService.Application.Contracts;
public sealed record PersistedRunEvent(long Sequence,RunStreamEvent Event);
public interface IRunEventRepository{Task<long> AppendAsync(RunStreamEvent evt,CancellationToken ct=default);Task<IReadOnlyList<PersistedRunEvent>> ListAsync(string runId,long afterSequence,int limit,CancellationToken ct=default);}
