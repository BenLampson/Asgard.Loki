namespace Asgard.Loki.LLM.Azure;

/// <summary>独立 Realtime JSON 消息的显式本地资源策略，不代表部署或模型能力。</summary>
public sealed record AzureRealtimeOptions
{
    /// <summary>单条完整收发 JSON 消息的 UTF-8 字节上限，默认 16 MiB，超限失败而非截断。</summary>
    public int MaximumMessageBytes { get; init; } = 16 * 1024 * 1024;
}
