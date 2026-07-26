using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Skills;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace AgentService.Infrastructure.AgentFramework;

/// <summary>
/// BYOK 路徑：OpenAI-compatible IChatClient → MAF ChatClientAgent。
/// 支援 OpenAI / Azure OpenAI / 自訂 endpoint（企業內部 LLM）。
/// 企業目前未開通供應商 endpoint，此路徑保留完整實作，開通即用。
/// </summary>
public sealed class ByokAgentFactory(
    IApiKeyStore apiKeyStore,
    ISkillProvider skillProvider,
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
            ChatOptions = new ChatOptions
            {
                Instructions = context.Instructions + context.SkillsPrompt,
                Tools = null,
            },
        });

        logger.LogDebug(
            "ByokAgentFactory 建立 Agent（profile={ProfileId}, model={Model}, skills={SkillCount}）",
            context.Profile.Id,
            context.ModelOverride ?? context.Profile.ModelId,
            skillProvider.ListSkills().Count);

        return agent;
    }

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
