using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.LLM.Dify.Internal;

/// <summary>固定应用身份及单次请求快照；不配置模型、缓存、会话调度或文件上传。</summary>
internal sealed class DifyChatTarget
{
    internal DifyChatOptions Options { get; }
    internal DifyChatMetadata Metadata { get; }
    private readonly string _key;
    /// <summary>真实凭据独立保存在供应商实例中，选项和公开元数据不包含密钥。</summary>
    internal DifyChatTarget(DifyChatOptions options, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.BaseAddress); ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var root = options.BaseAddress;
        if (!root.IsAbsoluteUri || root.Scheme is not ("http" or "https") || root.UserInfo.Length != 0 || root.Query.Length != 0 || root.Fragment.Length != 0)
            throw new ArgumentException("Dify API 根必须是无凭据、查询或片段的 HTTP(S) 绝对地址。");
        if (!Enum.IsDefined(options.Profile)) throw new ArgumentOutOfRangeException(nameof(options), "Dify 应用类别必须明确。");
        foreach (var text in new[] { options.TargetId, options.CredentialId, options.Version, options.HttpClientName, apiKey }) { ArgumentException.ThrowIfNullOrWhiteSpace(text); DifyJson.Text(text); }
        if (apiKey.Any(c => c is < '!' or > '~')) throw new ArgumentException("Dify Key 必须是不含空白/控制字符的 ASCII 值。");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumRequestBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumResponseBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumStreamBytes, 0); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumBufferedTailFrames, 0);
        Options = options with { BaseAddress = new(root.AbsoluteUri.TrimEnd('/') + "/") }; _key = apiKey;
        Metadata = new(Options.TargetId, Options.BaseAddress, Options.Profile, Options.CredentialId, Options.Version);
    }
    /// <summary>在任何工厂/异步边界前验证原始字符串和扩展值，再冻结全部正文。</summary>
    internal HttpRequestMessage Create(DifyChatRequest request, bool stream, out string? conversation)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters); ArgumentException.ThrowIfNullOrWhiteSpace(request.User); ArgumentNullException.ThrowIfNull(request.Query);
        if (!stream && Options.Profile == DifyChatProfile.Agent) throw new NotSupportedException("Dify Agent 应用仅支持原生 streaming。");
        DifyJson.Text(request.User); DifyJson.Text(request.Query); DifyJson.Element(request.Inputs);
        if (request.Inputs.ValueKind != JsonValueKind.Object) throw new ArgumentException("Dify inputs 必须是显式原生对象。");
        conversation = null;
        foreach (var property in request.Parameters)
        {
            DifyJson.Text(property.Key); DifyJson.Element(property.Value);
            if (new[] { "user", "query", "inputs", "response_mode" }.Any(n => n.Equals(property.Key, StringComparison.OrdinalIgnoreCase)) || property.Value.ValueKind == JsonValueKind.Undefined)
                throw new ArgumentException("Dify 原生扩展不能覆盖用户/查询/输入/响应模式或包含 Undefined。");
            if (property.Key.Equals("conversation_id", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Key != "conversation_id" || property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("Dify conversation_id 需要原生名称及字符串/null。");
                conversation = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }
        var body = JsonSerializer.SerializeToNode(request)!.AsObject(); body["response_mode"] = stream ? "streaming" : "blocking";
        var text = body.ToJsonString(); var utf8 = new UTF8Encoding(false, true);
        if (utf8.GetByteCount(text) > Options.MaximumRequestBytes) throw new InvalidDataException("Dify 请求超过 UTF-8 字节预算。");
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Options.BaseAddress, "chat-messages"));
        try
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key); message.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");
            message.Content = new ByteArrayContent(utf8.GetBytes(text)); message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" }; return message;
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>命名工厂不能引入另一种未绑定认证方式，显式 Bearer 始终由目标提供。</summary>
    internal static void Defaults(HttpClient client)
    { if (new[] { "api-key", "x-api-key", "x-goog-api-key" }.Any(client.DefaultRequestHeaders.Contains)) throw new InvalidOperationException("Dify HTTP 工厂包含未绑定认证头。"); }
    /// <summary>派发/读取/关闭边界检查固定方法与字面完整地址，不接受处理器换目标或重定向。</summary>
    internal static void Verify(HttpRequestMessage message, HttpResponseMessage response, string endpoint)
    {
        if (message.Method != HttpMethod.Post || message.RequestUri?.OriginalString != endpoint || response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != endpoint))
            throw new InvalidDataException("Dify 实际方法或完整目标发生变化。");
    }
}
