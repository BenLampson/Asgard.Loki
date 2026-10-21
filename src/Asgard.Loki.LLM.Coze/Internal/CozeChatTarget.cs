using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Coze.Internal;

/// <summary>固定原生服务/智能体/令牌与策略，不参与模型 Session、后台轮询或目标重选。</summary>
internal sealed class CozeChatTarget
{
    internal CozeChatOptions Options { get; }
    internal CozeChatMetadata Metadata { get; }
    internal string Identity { get; }
    private readonly string _key;
    /// <summary>首次调用前快照全部身份及策略，令牌不进入公开元数据或选项。</summary>
    internal CozeChatTarget(CozeChatOptions options, string apiToken)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.BaseAddress); ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);
        var root = options.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Coze v3 根必须是无凭据、查询或片段的 HTTP(S) 绝对地址。");
        foreach (var text in new[] { options.BotId, options.TargetId, options.CredentialId, options.Version, options.HttpClientName, apiToken })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); CozeJson.Text(text); }
        if (apiToken.Any(c => c is < '!' or > '~')) throw new ArgumentException("Coze Token 必须是不含空白/控制字符的 ASCII 值。");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumStreamBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTailFrames, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumTrackedMessages, 0);
        Options = options with { BaseAddress = new(root.AbsoluteUri.TrimEnd('/') + "/") }; _key = apiToken;
        Metadata = new(options.TargetId, Options.BaseAddress, options.BotId, options.CredentialId, options.Version);
        var binding = JsonSerializer.Serialize(new { Implementation = typeof(CozeChatClient).AssemblyQualifiedName, Options, Credential = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiToken))) });
        Identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
    }
    /// <summary>复制正文和明确查询 ID，方法只拥有 bot_id 与 stream，不填充保存/用户/消息默认值。</summary>
    internal HttpRequestMessage Create(CozeChatRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters); ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        CozeJson.Text(request.UserId); if (request.ConversationId is { } conversation) Id(conversation);
        foreach (var property in request.Parameters)
        {
            CozeJson.Text(property.Key);
            if (new[] { "bot_id", "user_id", "stream", "conversation_id" }.Any(n => n.Equals(property.Key, StringComparison.OrdinalIgnoreCase)) || property.Value.ValueKind == JsonValueKind.Undefined)
                throw new ArgumentException("Coze 原生扩展参数不能覆盖 Bot/用户/流/查询会话或包含 Undefined。");
            CozeJson.Element(property.Value);
        }
        var body = JsonSerializer.SerializeToNode(request)!.AsObject(); body["bot_id"] = Options.BotId; body["stream"] = stream;
        var text = body.ToJsonString(); var utf8 = new UTF8Encoding(false, true);
        if (utf8.GetByteCount(text) > Options.MaximumRequestBytes) throw new InvalidDataException("Coze 原生请求超过 UTF-8 字节预算。");
        var endpoint = new Uri(Options.BaseAddress, "chat");
        if (request.ConversationId is { } id) endpoint = new(endpoint.OriginalString + "?conversation_id=" + Uri.EscapeDataString(id));
        var result = Message(HttpMethod.Post, endpoint, stream);
        try { result.Content = new ByteArrayContent(utf8.GetBytes(text)); result.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; return result; }
        catch { result.Dispose(); throw; }
    }
    /// <summary>原生引用不能切换服务、智能体、凭据或策略，查询 ID 独立转义而不解析成 URL。</summary>
    internal HttpRequestMessage Query(CozeChatReference reference, bool messages)
    {
        Validate(reference);
        var endpoint = new Uri(Options.BaseAddress, messages ? "chat/message/list" : "chat/retrieve");
        return Message(HttpMethod.Get, new(endpoint.OriginalString + "?conversation_id=" + Uri.EscapeDataString(reference.ConversationId) + "&chat_id=" + Uri.EscapeDataString(reference.ChatId)), false);
    }
    /// <summary>借用的可持久化引用只提供身份与绑定信息，不代替上游权限检查。</summary>
    internal void Validate(CozeChatReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference); Id(reference.ConversationId); Id(reference.ChatId); Id(reference.TargetIdentity);
        if (!string.Equals(reference.TargetIdentity, Identity, StringComparison.Ordinal)) throw new ArgumentException("Coze 引用与当前完整原生目标不匹配。");
    }
    /// <summary>不裁剪或猜测原生 ID 格式，只拒绝空标识和原始坏 Unicode。</summary>
    internal static void Id(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); CozeJson.Text(value); }
    /// <summary>固定 Bearer 和对应媒体，不使用命名客户端的另一隐式认证方式。</summary>
    private HttpRequestMessage Message(HttpMethod method, Uri endpoint, bool stream)
    {
        var request = new HttpRequestMessage(method, endpoint); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        request.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json"); return request;
    }
    /// <summary>默认 Token 不覆盖选中身份；另一种默认 Key 不能成为未绑定凭据。</summary>
    internal static void Defaults(HttpClient client)
    { if (new[] { "api-key", "x-api-key", "x-goog-api-key" }.Any(client.DefaultRequestHeaders.Contains)) throw new InvalidOperationException("Coze HTTP 工厂包含未绑定认证头。"); }
    /// <summary>派发/读取/释放边界都验证原始方法和字面地址，防止重定向或处理器替换。</summary>
    internal static void Verify(HttpRequestMessage message, HttpResponseMessage response, string endpoint, HttpMethod method)
    {
        if (message.Method != method || message.RequestUri?.OriginalString != endpoint || response.RequestMessage is { } actual && (actual.Method != method || actual.RequestUri?.OriginalString != endpoint))
            throw new InvalidDataException("Coze 原生调用实际方法或完整目标发生变化。");
    }
}
