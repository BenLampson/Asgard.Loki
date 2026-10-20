using System.Net.WebSockets;

namespace Asgard.Loki.LLM.Xunfei;

/// <summary>借用的连接工厂；每次返回一个由调用操作拥有的独立 socket，不记录签名 URL。</summary>
public interface IXunfeiSparkWebSocketFactory
{
    /// <summary>稳定且无认证材料的连接配置身份；行为改变时必须改变该值。</summary>
    string BindingIdentity { get; }
    /// <summary>仅连接指定完整签名地址一次，不重试、改变目标、重用共享 socket 或记录 URL。</summary>
    Task<WebSocket> ConnectAsync(Uri address, CancellationToken cancellationToken = default);
}
