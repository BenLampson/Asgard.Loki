using System.Net.WebSockets;

namespace Asgard.Loki.LLM.Azure;

/// <summary>应用借出的单次 Realtime 握手工厂；成功返回的已打开 socket 交由连接拥有。</summary>
public interface IAzureRealtimeWebSocketFactory
{
    /// <summary>稳定端点/代理/认证策略身份，不包含短期令牌，原实例不得变更。</summary>
    string BindingIdentity => GetType().AssemblyQualifiedName ?? GetType().FullName!;
    /// <summary>只握手一次；不得自动重试、重定向到其他目标或记录认证头，失败时关闭自行创建的资源。</summary>
    Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);
}
