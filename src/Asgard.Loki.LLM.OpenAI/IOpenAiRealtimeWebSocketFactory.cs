using System.Net.WebSockets;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>借用的原生 Realtime 握手工厂；调用者返回的已打开 socket 由连接拥有。</summary>
public interface IOpenAiRealtimeWebSocketFactory
{
    /// <summary>影响完整目标绑定的稳定配置身份；不得包含短期令牌。</summary>
    string BindingIdentity => GetType().AssemblyQualifiedName ?? GetType().FullName!;
    /// <summary>只连接一次，不重试；不得记录认证头或把头写入错误正文。</summary>
    Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);
}
