namespace Asgard.Loki.LLM;

/// <summary>打开调用者驱动的原生 Realtime 连接，不启动代理、音频设备或工具循环。</summary>
public interface IRealtimeClient
{
    /// <summary>绑定完整目标并建立一次连接；缓存不恢复远端会话，不自动重连或重放。</summary>
    Task<IRealtimeConnection> OpenAsync(string model, LlmCallOptions options, CancellationToken cancellationToken = default);
}
