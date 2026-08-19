using System.Runtime.CompilerServices;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Host.RestEndpoints;
using AgentService.Infrastructure.AgentRuntime;
using AgentService.Infrastructure.Orchestration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentService.CoreRegression;

/// <summary>驗證整輪期限、外部取消與 SSE 錯誤契約不會互相混淆。</summary>
public sealed class ConversationExecutionTimeoutRegressionTests
{
    [Fact]
    public void 一般與專案對話使用不同的整輪期限()
    {
        var options = new ConversationRuntimeOptions
        {
            GeneralTimeoutSeconds = 300,
            ProjectAnalysisTimeoutSeconds = 600,
        };

        Assert.Equal(TimeSpan.FromMinutes(5), options.ResolveTimeout(false));
        Assert.Equal(TimeSpan.FromMinutes(10), options.ResolveTimeout(true));
    }

    [Fact]
    public async Task 只有整輪期限到期才回傳TurnTimeout()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = CreateService(serviceProvider);
        var request = CreateRequest(
            WaitUntilCancelledAsync,
            executionTimeout: TimeSpan.FromMilliseconds(40));

        var events = await CollectAsync(service.ExecuteAsync(request));

        var error = Assert.Single(events.OfType<ConversationErrorEvent>());
        Assert.Equal(ConversationErrorCodes.TurnTimeout, error.Code);
        Assert.True(error.Retryable);
        Assert.Equal("preparing", error.Stage);
    }

    [Fact]
    public async Task 相依元件自行取消不會誤報成整輪逾時()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = CreateService(serviceProvider);
        var request = CreateRequest(
            (_, _) => Task.FromException<ConversationPreparation>(
                new OperationCanceledException("模擬 Provider 自行逾時")),
            executionTimeout: TimeSpan.FromSeconds(5));

        var events = await CollectAsync(service.ExecuteAsync(request));

        var error = Assert.Single(events.OfType<ConversationErrorEvent>());
        Assert.Equal(ConversationErrorCodes.DependencyTimeout, error.Code);
        Assert.NotEqual(ConversationErrorCodes.TurnTimeout, error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task 相依元件TimeoutException會回傳DependencyTimeout()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = CreateService(serviceProvider);
        var request = CreateRequest(
            (_, _) => Task.FromException<ConversationPreparation>(
                new TimeoutException("模擬 Provider 連線逾時")),
            executionTimeout: TimeSpan.FromSeconds(5));

        var events = await CollectAsync(service.ExecuteAsync(request));

        var error = Assert.Single(events.OfType<ConversationErrorEvent>());
        Assert.Equal(ConversationErrorCodes.DependencyTimeout, error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task 使用者取消不會被回報為逾時錯誤()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = CreateService(serviceProvider);
        var request = CreateRequest(
            WaitUntilCancelledAsync,
            executionTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        var events = await CollectAsync(service.ExecuteAsync(request, cancellation.Token));

        Assert.Empty(events.OfType<ConversationErrorEvent>());
    }

    [Fact]
    public async Task Sse錯誤會保留Code重試性與階段()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ConversationEndpointSupport.WriteStreamAsync(
            context,
            SingleEventAsync(new ConversationErrorEvent(
                "專案解析超過本輪執行期限。",
                ConversationErrorCodes.TurnTimeout,
                Retryable: true,
                Stage: "tool_execution")),
            CancellationToken.None);

        body.Position = 0;
        var payload = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("\"code\":\"turn_timeout\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"retryable\":true", payload, StringComparison.Ordinal);
        Assert.Contains("\"stage\":\"tool_execution\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sse開始事件會傳遞固定GraphVersion()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ConversationEndpointSupport.WriteStreamAsync(
            context,
            SingleEventAsync(new ConversationStartedEvent(
                "provider",
                "model",
                "run-id",
                "ready",
                null,
                "graph-fixed")),
            CancellationToken.None);

        body.Position = 0;
        var payload = await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("\"graphVersion\":\"graph-fixed\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 未預期例外不得把內部訊息傳給前端()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = CreateService(serviceProvider);
        var request = CreateRequest(
            (_, _) => Task.FromException<ConversationPreparation>(
                new InvalidOperationException("D:\\secret\\provider.json")),
            TimeSpan.FromSeconds(5));

        var events = await CollectAsync(service.ExecuteAsync(request));

        var error = Assert.Single(events.OfType<ConversationErrorEvent>());
        Assert.Equal(ConversationErrorCodes.AgentExecutionFailed, error.Code);
        Assert.DoesNotContain("secret", error.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider.json", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 同一對話已有執行中要求時_第二個要求應立即回傳Busy()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = CreateService(serviceProvider);
        var conversation = new ConversationEntity();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancellation = new CancellationTokenSource();
        var firstRequest = CreateRequest(
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("測試預期應先取消等待。");
            },
            TimeSpan.FromSeconds(5)) with { Conversation = conversation };
        var secondRequest = CreateRequest(
            (_, _) => Task.FromResult(new ConversationPreparation(
                "測試問題", "測試指示", string.Empty, [])),
            TimeSpan.FromSeconds(5)) with { Conversation = conversation };

        var firstExecution = CollectAsync(service.ExecuteAsync(firstRequest, firstCancellation.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondEvents = await CollectAsync(service.ExecuteAsync(secondRequest));
        firstCancellation.Cancel();
        await firstExecution;

        var error = Assert.Single(secondEvents.OfType<ConversationErrorEvent>());
        Assert.Equal(ConversationErrorCodes.ConversationBusy, error.Code);
        Assert.True(error.Retryable);
    }

    private static ConversationExecutionService CreateService(IServiceProvider serviceProvider) =>
        new(
            new NoOpConversationRepository(),
            new AgentRuntime([], NullLogger<AgentRuntime>.Instance),
            new EmptySkillProvider(),
            new NoOpProjectJobQueue(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationRuntimeOptions()),
            NullLogger<ConversationExecutionService>.Instance);

    private static ConversationExecutionRequest CreateRequest(
        Func<AgentActivityReporter, CancellationToken, Task<ConversationPreparation>> prepare,
        TimeSpan executionTimeout) =>
        new(
            new ConversationEntity(),
            new SendMessageRequest("測試問題"),
            new ModelProviderProfile
            {
                Id = "test-provider",
                DisplayName = "測試 Provider",
            },
            ModelId: "test-model",
            Prepare: prepare,
            ExecutionTimeout: executionTimeout);

    private static async Task<ConversationPreparation> WaitUntilCancelledAsync(
        AgentActivityReporter _,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("測試預期應先取消等待。");
    }

    private static async Task<List<ConversationStreamEvent>> CollectAsync(
        IAsyncEnumerable<ConversationStreamEvent> stream)
    {
        var events = new List<ConversationStreamEvent>();
        await foreach (var item in stream)
            events.Add(item);
        return events;
    }

    private static async IAsyncEnumerable<ConversationStreamEvent> SingleEventAsync(
        ConversationStreamEvent value,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return value;
    }

    private sealed class EmptySkillProvider : ISkillProvider
    {
        public IReadOnlyList<SkillDefinition> ListSkills() => [];
        public void Refresh() { }
    }

    private sealed class NoOpProjectJobQueue : IProjectJobQueue
    {
        public ValueTask EnqueueAsync(
            Func<CancellationToken, Task> work,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class NoOpConversationRepository : IConversationRepository
    {
        public Task<List<ConversationEntity>> ListGeneralAsync(CancellationToken ct = default) =>
            Task.FromResult<List<ConversationEntity>>([]);

        public Task<List<ConversationEntity>> ListProjectAsync(
            string projectId,
            CancellationToken ct = default) =>
            Task.FromResult<List<ConversationEntity>>([]);

        public Task<ConversationEntity?> GetGeneralAsync(
            string id,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationEntity?>(null);

        public Task<ConversationEntity?> GetProjectAsync(
            string projectId,
            string id,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationEntity?>(null);

        public Task<ConversationEntity> CreateGeneralAsync(
            string? providerProfileId = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ConversationEntity());

        public Task<ConversationEntity> CreateProjectAsync(
            string projectId,
            string? providerProfileId = null,
            CancellationToken ct = default) =>
            Task.FromResult(new ConversationEntity { ProjectId = projectId });

        public Task DeleteAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string> AddMessageAsync(
            string conversationId,
            MessageRole role,
            string content,
            CancellationToken ct = default) =>
            Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task<string> AddMessageAsync(
            string conversationId,
            MessageRole role,
            string content,
            string? turnId,
            CancellationToken ct = default) =>
            Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task<ConversationTurnEntity?> GetTurnAsync(
            string conversationId,
            string turnId,
            CancellationToken ct = default) =>
            Task.FromResult<ConversationTurnEntity?>(null);

        public Task<ConversationTurnEntity> BeginTurnAsync(
            string conversationId,
            string turnId,
            string userMessage,
            string providerProfileId,
            string? modelId,
            CancellationToken ct = default) =>
            Task.FromResult(new ConversationTurnEntity
            {
                Id = turnId,
                ConversationId = conversationId,
                UserMessageId = Guid.NewGuid().ToString("N"),
                ProviderProfileId = providerProfileId,
                ModelId = modelId,
                UserMessageHash = "test",
            });

        public Task<MessageEntity?> GetMessageAsync(
            string messageId,
            CancellationToken ct = default) =>
            Task.FromResult<MessageEntity?>(null);

        public Task CompleteTurnAsync(
            string conversationId,
            string turnId,
            string assistantMessageId,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task FailTurnAsync(
            string conversationId,
            string turnId,
            ConversationTurnStatus status,
            string errorCode,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetTitleAsync(
            string conversationId,
            string title,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
