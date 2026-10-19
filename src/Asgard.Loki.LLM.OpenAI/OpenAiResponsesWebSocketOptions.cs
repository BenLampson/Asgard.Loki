namespace Asgard.Loki.LLM.OpenAI;

/// <summary>原生连接的显式内存上限；这些是客户端资源策略，不是上游模型能力声明。</summary>
public sealed record OpenAiResponsesWebSocketOptions
{
    /// <summary>每条完整收发 JSON 消息的 UTF-8 字节上限，默认 16 MiB。</summary>
    public int MaximumMessageBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>等待响应或控制确认的提交数量上限，默认 256。</summary>
    public int MaximumPendingRequests { get; init; } = 256;
    /// <summary>连接保留的响应身份数量上限，默认 4096；达到上限后由调用者显式建立新连接。</summary>
    public int MaximumTrackedResponses { get; init; } = 4096;
}
