namespace Asgard.Loki.LLM.Volcengine;

/// <summary>方舟原生 HTTP 本地资源策略，不选择云模型、应用或工具能力。</summary>
public sealed record VolcengineHttpOptions
{
    /// <summary>完整原生 JSON 请求 UTF-8 字节上限，默认三十二 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整 JSON/错误或整个 SSE 原始字节上限，默认三十二 MiB。</summary>
    public int MaximumBodyBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>首个可能结束阶段起保留的原生帧数上限，默认一百二十八。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>流内不同原生 choice 索引数上限，默认一百二十八；不按远端索引分配数组。</summary>
    public int MaximumTrackedChoices { get; init; } = 128;
}
