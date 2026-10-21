using System.Net.WebSockets;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>可替换的原生 Responses 连接工厂；供应商借用工厂，连接拥有返回的 socket。</summary>
public interface IOpenAiResponsesWebSocketFactory
{
    /// <summary>影响目标绑定的工厂身份；配置语义变化时调用者实现必须改变该值。</summary>
    string BindingIdentity => GetType().AssemblyQualifiedName ?? GetType().FullName!;
    /// <summary>建立一次连接，不重试；认证头仅用于本次握手，不应写入日志或错误正文。</summary>
    Task<WebSocket> ConnectAsync(Uri address, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default);
}
