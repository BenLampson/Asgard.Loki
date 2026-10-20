namespace Asgard.Loki.LLM.OpenAI;

/// <summary>Realtime 原生 JSON 连接的显式本地资源策略，不代表上游模型能力。</summary>
public sealed record OpenAiRealtimeOptions
{
    /// <summary>单条完整收发 JSON 消息的 UTF-8 字节上限，默认 16 MiB。</summary>
    public int MaximumMessageBytes { get; init; } = 16 * 1024 * 1024;
}
