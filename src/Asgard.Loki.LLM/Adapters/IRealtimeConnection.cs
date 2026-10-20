namespace Asgard.Loki.LLM;

/// <summary>原生 JSON 双工连接；不聚合音频、执行工具、推断生成成功或恢复远端状态。</summary>
public interface IRealtimeConnection : IAsyncDisposable
{
    /// <summary>连接固定的公共模型及完整上游身份，不包含凭据。</summary>
    LlmResultMetadata Metadata { get; }
    /// <summary>同步复制原生事件再等待写入；未知字段及工具关联由调用者保留。</summary>
    ValueTask SendAsync(JsonObject message, CancellationToken cancellationToken = default);
    /// <summary>单读取者接收原生事件，包括错误、失败及未知事件；关闭不等于响应成功。</summary>
    IAsyncEnumerable<RealtimeEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
    /// <summary>显式关闭传输，不发送生成命令，也不等待或合成模型终态。</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
