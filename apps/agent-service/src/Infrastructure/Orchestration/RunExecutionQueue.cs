using System.Threading.Channels;
using AgentService.Application.Contracts;
namespace AgentService.Infrastructure.Orchestration;
public sealed class RunExecutionQueue(ILogger<RunExecutionQueue> logger):BackgroundService,IRunExecutionQueue
{
    private readonly Channel<Func<CancellationToken,Task>> _queue=Channel.CreateUnbounded<Func<CancellationToken,Task>>(new(){SingleReader=true});
    public ValueTask EnqueueAsync(Func<CancellationToken,Task> work,CancellationToken ct=default)=>_queue.Writer.WriteAsync(work,ct);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach(var work in _queue.Reader.ReadAllAsync(stoppingToken)){try{await work(stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}catch(Exception ex){logger.LogError(ex,"Queued run execution failed");}}
    }
}
