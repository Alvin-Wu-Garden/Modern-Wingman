using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Anthropic;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

#pragma warning disable OPENAI001 // MAF 1.13 的 Responses adapter 仍標記為 preview，本產品已明確選用。

namespace AgentService.Infrastructure.AgentRuntime.Factories;

/// <summary>
/// 直接 BYOK Agent 工廠。
/// <para>
/// ProviderKind 只表示「不走 Copilot CLI」；實際 HTTP 協定由
/// <see cref="ProviderProtocol"/> 決定。這避免把 Anthropic、Azure OpenAI、
/// OpenAI-compatible endpoint 全部誤送進同一個 Copilot ProviderConfig。
/// </para>
/// </summary>
public sealed class ByokAgentFactory(
    IApiKeyStore apiKeyStore,
    ILogger<ByokAgentFactory> logger) : IAgentFactory
{
    public ProviderKind Kind => ProviderKind.CopilotByok;

    /// <summary>依 profile 的協定建立 MAF Agent；缺少 API Key 時回傳 null。</summary>
    public AIAgent? CreateAgent(AgentCreationContext context)
    {
        var apiKey = apiKeyStore.Get(context.Profile.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning(
                "BYOK 憑證不存在。ProfileId={ProfileId}, Protocol={Protocol}",
                context.Profile.Id,
                context.Profile.EffectiveProtocol);
            return null;
        }

        var modelId = context.ModelOverride ?? context.Profile.ModelId;
        var options = BuildChatOptions(context);
        var agent = context.Profile.EffectiveProtocol switch
        {
            ProviderProtocol.OpenAI => BuildOpenAIAgent(context.Profile, apiKey, modelId, options),
            ProviderProtocol.OpenAICompatible => BuildOpenAICompatibleAgent(context.Profile, apiKey, modelId, options),
            ProviderProtocol.AzureOpenAI => BuildAzureOpenAIAgent(context.Profile, apiKey, modelId, options),
            ProviderProtocol.Anthropic => BuildAnthropicAgent(context.Profile, apiKey, modelId, options),
            _ => throw new InvalidOperationException(
                $"Provider Profile「{context.Profile.Id}」的 BYOK 協定不受支援：{context.Profile.Protocol}。"),
        };

        logger.LogDebug(
            "BYOK Agent 已建立。ProfileId={ProfileId}, Protocol={Protocol}, Model={Model}, ToolCount={ToolCount}",
            context.Profile.Id,
            context.Profile.EffectiveProtocol,
            modelId ?? "(default)",
            context.Tools.Count);
        return agent;
    }

    /// <summary>
    /// 將共用 Agent context 轉為 MAF ChatClientAgentOptions。
    /// 只帶入呼叫端明確提供的工具，避免一般對話意外取得 GraphRAG 工具。
    /// </summary>
    internal static ChatClientAgentOptions BuildChatOptions(AgentCreationContext context) =>
        new()
        {
            Name = "WingmanAgent",
            ChatOptions = new ChatOptions
            {
                // Anthropic MAF adapter 從 ChatOptions 取得模型；OpenAI／Azure
                // 雖在 GetChatClient 時指定，仍同步設定可避免不同 adapter 漏掉模型。
                ModelId = context.ModelOverride ?? context.Profile.ModelId,
                Instructions = context.Instructions + context.SkillsPrompt,
                Tools = context.Tools.Count == 0
                    ? null
                    : context.Tools.Cast<AITool>().ToList(),
            },
        };

    /// <summary>建立 OpenAI 官方 Chat Completions 或 Responses Agent。</summary>
    private static AIAgent BuildOpenAIAgent(
        ModelProviderProfile profile,
        string apiKey,
        string? modelId,
        ChatClientAgentOptions options)
    {
        var client = CreateOpenAIClient(profile, apiKey);
        return profile.WireApiMode == ModelWireApi.Responses
            ? client.GetResponsesClient().AsAIAgent(options, modelId)
            : client.GetChatClient(modelId ?? "gpt-4o-mini").AsAIAgent(options);
    }

    /// <summary>建立自訂 OpenAI-compatible Agent；此路徑固定使用 Chat Completions。</summary>
    private static AIAgent BuildOpenAICompatibleAgent(
        ModelProviderProfile profile,
        string apiKey,
        string? modelId,
        ChatClientAgentOptions options)
    {
        if (string.IsNullOrWhiteSpace(profile.BaseUrl))
            throw new InvalidOperationException(
                $"OpenAI-compatible Profile「{profile.Id}」必須設定 BaseUrl。");

        // 自訂端點只保證 OpenAI Chat Completions，不把 Responses 欄位誤傳給第三方服務。
        var client = CreateOpenAIClient(profile, apiKey);
        return client.GetChatClient(modelId ?? "default").AsAIAgent(options);
    }

    /// <summary>使用 Azure.AI.OpenAI 原生 API-key client 建立 Azure OpenAI Agent。</summary>
    private static AIAgent BuildAzureOpenAIAgent(
        ModelProviderProfile profile,
        string apiKey,
        string? modelId,
        ChatClientAgentOptions options)
    {
        if (string.IsNullOrWhiteSpace(profile.BaseUrl) ||
            !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException(
                $"Azure OpenAI Profile「{profile.Id}」必須設定有效的 resource endpoint。");

        // API-key 模式不使用 Azure.Identity，也不讀取環境變數或登入狀態。
        var client = new AzureOpenAIClient(
            endpoint,
            new System.ClientModel.ApiKeyCredential(apiKey));
        return client.GetChatClient(modelId ?? "default").AsAIAgent(options);
    }

    /// <summary>使用 Microsoft.Agents.AI.Anthropic 的官方 MAF adapter。</summary>
    private static AIAgent BuildAnthropicAgent(
        ModelProviderProfile profile,
        string apiKey,
        string? modelId,
        ChatClientAgentOptions options)
    {
        var client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = profile.BaseUrl ?? "https://api.anthropic.com",
        };

        return client.AsAIAgent(options);
    }

    private static OpenAIClient CreateOpenAIClient(
        ModelProviderProfile profile,
        string apiKey)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
            options.Endpoint = new Uri(profile.BaseUrl);
        return new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey),
            options);
    }
}
