using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace AgentService.Infrastructure.AgentRuntime.Factories;

/// <summary>
/// BYOK 路徑：OpenAI-compatible IChatClient → MAF ChatClientAgent。
/// 支援 OpenAI / Azure OpenAI / 自訂 endpoint（企業內部 LLM）。
/// 企業目前未開通供應商 endpoint，此路徑保留完整實作，開通即用。
/// </summary>
public sealed class ByokAgentFactory(
    IApiKeyStore apiKeyStore,
    ILogger<ByokAgentFactory> logger) : IAgentFactory
{
    public ProviderKind Kind => ProviderKind.CopilotByok;

    public AIAgent? CreateAgent(AgentCreationContext context)
    {
        var chatClient = BuildOpenAIChatClient(context.Profile, context.ModelOverride);
        if (chatClient is null)
        {
            logger.LogError("Profile [{ProfileId}] 無法建立 IChatClient（API Key 遺失）", context.Profile.Id);
            return null;
        }

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "WingmanAgent",
            ChatOptions = BuildChatOptions(context),
        });

        logger.LogDebug(
            "ByokAgentFactory 建立 Agent。ProfileId={ProfileId}, Model={Model}, ToolCount={ToolCount}",
            context.Profile.Id,
            context.ModelOverride ?? context.Profile.ModelId,
            context.Tools.Count);

        return agent;
    }

    /// <summary>
    /// 將共用 Agent context 轉為 BYOK ChatOptions。
    /// 保留原始 <see cref="AIFunction"/> 實例，讓 ChatClientAgent 能實際呼叫函式，
    /// 而不是只收到無法執行的工具宣告。
    /// </summary>
    /// <param name="context">包含本輪系統指示及受控唯讀工具的建立資訊。</param>
    /// <returns>只帶入呼叫端明確提供工具的 ChatOptions。</returns>
    internal static ChatOptions BuildChatOptions(AgentCreationContext context) =>
        new()
        {
            Instructions = context.Instructions + context.SkillsPrompt,
            Tools = context.Tools.Count == 0
                ? null
                : context.Tools.Cast<AITool>().ToList(),
        };

    private IChatClient? BuildOpenAIChatClient(ModelProviderProfile profile, string? modelOverride)
    {
        var apiKey = apiKeyStore.Get(profile.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var openAIClientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
            openAIClientOptions.Endpoint = new Uri(profile.BaseUrl);

        var openAIClient = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey), openAIClientOptions);
        var modelId = modelOverride ?? profile.ModelId ?? "gpt-4o-mini";

        return openAIClient.GetChatClient(modelId).AsIChatClient();
    }
}
