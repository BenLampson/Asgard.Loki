namespace Asgard.Loki.LLM.Google.Internal;

/// <summary>一次明确 HTTP 操作的地址、正文和认证快照；不保存历史、不解析原生终态。</summary>
internal sealed class GoogleConfiguredHttpCall
{
    private static readonly string[] AuthenticationHeaders = ["Authorization", "api-key", "x-api-key", "x-goog-api-key", "x-amz-date", "x-amz-security-token", "x-amz-content-sha256", "Proxy-Authorization", "Cookie"];
    private readonly string? _header;
    private readonly string? _value;
    private readonly string _endpoint;
    private readonly string _host;
    private readonly string _accept;
    private readonly HttpContent _content;
    internal HttpRequestMessage Request { get; }
    internal GoogleConfiguredHttpCall(byte[] body, LlmAdapterContext context, GoogleHttpTransportOptions options, GoogleHttpAuthentication authentication, bool stream)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(body);
        var anonymous = authentication == GoogleHttpAuthentication.None;
        if (context.Credential.Kind != (anonymous ? LlmCredentialKind.Anonymous : LlmCredentialKind.ApiKey)) throw new NotSupportedException("显式 HTTP 认证与目标凭据种类不匹配。");
        if (!anonymous)
        {
            var key = context.Credential.ApiKey; GoogleConfiguredHttpEndpoint.Text(key);
            if (authentication != GoogleHttpAuthentication.Query && key.Any(c => c is < ' ' or > '~')) throw new ArgumentException("HTTP 认证头需要可打印 ASCII 密钥。");
            if (authentication == GoogleHttpAuthentication.Bearer && key.Any(char.IsWhiteSpace)) throw new ArgumentException("Bearer 密钥不能包含空白。");
            _value = authentication == GoogleHttpAuthentication.Bearer ? "Bearer " + key : options.CredentialPrefix + key + options.CredentialSuffix;
            GoogleConfiguredHttpEndpoint.Text(_value);
            if (authentication == GoogleHttpAuthentication.Header && _value.Any(c => c > '~')) throw new ArgumentException("HTTP 认证头需要可打印 ASCII 值。");
            _header = authentication switch { GoogleHttpAuthentication.ApiKey => "x-goog-api-key", GoogleHttpAuthentication.Bearer => "Authorization", GoogleHttpAuthentication.Header => options.CredentialName, _ => null };
        }
        var path = GoogleConfiguredHttpEndpoint.ModelOperation(options, context.Target.UpstreamModel, stream);
        var endpoint = GoogleConfiguredHttpEndpoint.Create(context.Target.BaseAddress, path, options, _value, authentication); _endpoint = endpoint.OriginalString; _host = endpoint.Authority; _accept = stream ? "text/event-stream" : "application/json";
        Request = new(HttpMethod.Post, endpoint); _content = new ByteArrayContent(body.ToArray());
        try
        {
            Request.Content = _content; _content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            Request.Headers.Host = _host; Request.Headers.Accept.ParseAdd(_accept);
            if (_header is not null && !Request.Headers.TryAddWithoutValidation(_header, _value)) throw new ArgumentException("认证名称不是有效请求头。");
        }
        catch { Request.Dispose(); throw; }
    }
    internal void BeforeSend(HttpClient client)
    {
        foreach (var name in AuthenticationHeaders)
            if (!name.Equals(_header, StringComparison.OrdinalIgnoreCase) && client.DefaultRequestHeaders.Contains(name)) throw new InvalidOperationException("命名客户端包含未选择的认证或签名默认头。");
        Verify(null);
    }
    internal void Verify(HttpResponseMessage? response)
    {
        if (Request.Method != HttpMethod.Post || Request.RequestUri?.OriginalString != _endpoint || !Headers(Request)
            || response?.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != _endpoint || !Headers(actual))
            || !ReferenceEquals(Request.Content, _content) || _content.Headers.ContentType?.ToString() != "application/json; charset=utf-8") throw new InvalidDataException("明确 HTTP 操作的实际目标、认证、方法或正文改变。");
    }
    private bool Headers(HttpRequestMessage message)
    {
        if (message.Headers.Host != _host || message.Headers.Accept.Count != 1 || message.Headers.Accept.Single().MediaType != _accept) return false;
        foreach (var name in AuthenticationHeaders) if (!name.Equals(_header, StringComparison.OrdinalIgnoreCase) && message.Headers.Contains(name)) return false;
        return _header is null || message.Headers.NonValidated.TryGetValues(_header, out var values) && values.SequenceEqual([_value!]);
    }
}
