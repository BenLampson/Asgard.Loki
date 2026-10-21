namespace Asgard.Loki.LLM.Xunfei;

/// <summary>星火供应商的固定 AppId 和有界传输策略；实际端点/domain 仍属于完整目标。</summary>
public sealed record XunfeiSparkOptions
{
    /// <summary>显式原生 AppId，稳定配置改变会改变 Session 指纹，不作为轮换 Key。</summary>
    public required string AppId { get; init; }
    /// <summary>注入 AppId 后的完整原生请求字节上限。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>每个完整原生 JSON 消息的字节上限。</summary>
    public int MaximumMessageBytes { get; init; } = 1024 * 1024;
    /// <summary>整次调用接收的消息负载字节上限，不包括网络控制帧。</summary>
    public long MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整逻辑 JSON 消息数量上限，包含插件和元数据。</summary>
    public int MaximumEvents { get; init; } = 8192;
    /// <summary>单条消息传输分片数上限，防止空分片使字节限制失效。</summary>
    public int MaximumFragmentsPerMessage { get; init; } = 8192;
    /// <summary>原生终态窗口消息数上限，不截断尾部。</summary>
    public int MaximumBufferedTerminalEvents { get; init; } = 128;
    /// <summary>一次握手的显式超时，不触发重试。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>主动关闭到对端正常确认的总时间上限。</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
