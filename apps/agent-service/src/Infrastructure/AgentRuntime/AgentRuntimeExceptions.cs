namespace AgentService.Infrastructure.AgentRuntime;

/// <summary>Agent Runtime 可安全回報給對話層的結構化錯誤。</summary>
public sealed class AgentRuntimeException(
    string code,
    string userMessage,
    bool retryable = false,
    Exception? innerException = null) : Exception(userMessage, innerException)
{
    /// <summary>穩定錯誤代碼，不包含上游例外文字。</summary>
    public string Code { get; } = code;

    /// <summary>可直接顯示給使用者的繁體中文訊息。</summary>
    public string UserMessage { get; } = userMessage;

    /// <summary>是否可用相同輸入重試。</summary>
    public bool Retryable { get; } = retryable;
}
