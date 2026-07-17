namespace AgentService.Application.Contracts;
public interface IRunExecutionQueue
{
    ValueTask EnqueueAsync(Func<CancellationToken,Task> work,CancellationToken ct=default);
}
