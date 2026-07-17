using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Orchestration;
using AgentService.Infrastructure.Persistence;
using AgentService.Infrastructure.Skills;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class AdvancedAgentCapabilitiesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wingman-advanced-" + Guid.NewGuid().ToString("N"));
    public AdvancedAgentCapabilitiesTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task PluginCatalog_ValidatesManifestAndRejectsEscapingPaths()
    {
        var plugin = Path.Combine(root, "sample");
        Directory.CreateDirectory(Path.Combine(plugin, ".wingman-plugin"));
        await File.WriteAllTextAsync(Path.Combine(plugin, ".wingman-plugin", "plugin.json"),
            "{\"id\":\"sample\",\"name\":\"Sample\",\"version\":\"1.0.0\",\"schemaVersion\":1,\"skills\":[\"skills/demo\"],\"mcpServers\":[],\"hooks\":[],\"assets\":[]}");
        var catalog = new PluginCatalog(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Plugins:Root",root}}).Build());

        var manifest = await catalog.ValidateAsync(plugin);
        Assert.Equal("sample", manifest.Id);
        Assert.Equal("skills/demo", Assert.Single(manifest.Skills));

        await File.WriteAllTextAsync(Path.Combine(plugin, ".wingman-plugin", "plugin.json"),
            "{\"id\":\"sample\",\"name\":\"Sample\",\"version\":\"1\",\"schemaVersion\":1,\"skills\":[\"../escape\"]}");
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.ValidateAsync(plugin));
    }

    [Fact]
    public async Task HookDispatcher_IsolatesFailuresAndContinues()
    {
        var called = new RecordingHook();
        var dispatcher = new AgentHookDispatcher([new ThrowingHook(), called], NullLogger<AgentHookDispatcher>.Instance);
        await dispatcher.DispatchAsync(new(AgentHookStage.BeforeTool, "run"));
        Assert.Equal(1, called.Count);
    }

    [Fact]
    public async Task SubagentCoordinator_StartsParallelChildrenWithParentLink()
    {
        var parent = new RunEntity("parent") { SessionId="session", UserMessage="parent", WorkspacePath=root, Mode=AgentMode.Ask };
        var repository = new MemoryRunRepository(parent);
        var orchestrator = new RecordingOrchestrator();
        var children = await new SubagentCoordinator(repository, orchestrator).StartParallelAsync("parent",
            [new("reviewer","review"),new("tester","test")]);
        Assert.Equal(2, children.Count);
        Assert.All(orchestrator.Commands, command => Assert.Equal("parent", command.ParentRunId));
        Assert.Equal(["reviewer","tester"], orchestrator.Commands.Select(command=>command.AgentRole));
    }

    [Fact]
    public async Task ScheduleStore_RoundTripsPersistentSchedule()
    {
        await using var connection=new SqliteConnection("DataSource=:memory:");await connection.OpenAsync();
        var options=new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using(var db=new AppDbContext(options))await db.Database.EnsureCreatedAsync();
        var store=new AgentScheduleStore(new Factory(options));
        var schedule=new AgentSchedule{Name="Nightly",Task="verify",WorkspacePath=root,NextRunAt=DateTimeOffset.UtcNow.AddMinutes(-1)};
        await store.SaveAsync(schedule);
        Assert.Equal(schedule.Id,(await store.ListDueAsync(DateTimeOffset.UtcNow)).Single().Id);
    }

    [Fact]
    public void DpapiSecretProtector_RoundTripsForCurrentUser()
    {
        var protector=new DpapiSecretProtector();var protectedSecret=protector.Protect("token-value");
        Assert.Equal("token-value",protector.Unprotect(protectedSecret.Value,protectedSecret.Scheme));
        if(OperatingSystem.IsWindows()){Assert.Equal("dpapi-current-user-v1",protectedSecret.Scheme);Assert.NotEqual("token-value",protectedSecret.Value);}
    }

    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
    private sealed class RecordingHook:IAgentHook{public string Name=>"record";public int Count{get;private set;}public ValueTask InvokeAsync(AgentHookContext context,CancellationToken ct=default){Count++;return ValueTask.CompletedTask;}}
    private sealed class ThrowingHook:IAgentHook{public string Name=>"throw";public ValueTask InvokeAsync(AgentHookContext context,CancellationToken ct=default)=>throw new InvalidOperationException("hook failed");}
    private sealed class Factory(DbContextOptions<AppDbContext> options):IDbContextFactory<AppDbContext>{public AppDbContext CreateDbContext()=>new(options);}
    private sealed class MemoryRunRepository(RunEntity run):IRunRepository
    {public Task SaveAsync(RunEntity value,CancellationToken ct=default)=>Task.CompletedTask;public Task<RunEntity?> GetAsync(string id,CancellationToken ct=default)=>Task.FromResult<RunEntity?>(id==run.Id?run:null);public Task<IReadOnlyList<RunEntity>> ListBySessionAsync(string id,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<RunEntity>>([]);public Task<IReadOnlyList<RunEntity>> ListByStatusesAsync(IReadOnlyCollection<RunStatus> statuses,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<RunEntity>>([]);}
    private sealed class RecordingOrchestrator:IRunOrchestrator
    {public List<CreateRunCommand> Commands{get;}=[];public Task<RunEntity> StartRunAsync(CreateRunCommand command,CancellationToken ct=default){Commands.Add(command);return Task.FromResult(new RunEntity{SessionId=command.SessionId,UserMessage=command.UserMessage,ParentRunId=command.ParentRunId,AgentRole=command.AgentRole});}public Task CancelRunAsync(string id,CancellationToken ct=default)=>Task.CompletedTask;public Task PauseRunAsync(string id,CancellationToken ct=default)=>Task.CompletedTask;public Task<RunEntity> ResumeRunAsync(string id,CancellationToken ct=default)=>throw new NotSupportedException();public Task<RunEntity> RetryRunAsync(string id,string? providerProfileId=null,CancellationToken ct=default)=>throw new NotSupportedException();public RunEntity? GetRun(string id)=>null;}
}
