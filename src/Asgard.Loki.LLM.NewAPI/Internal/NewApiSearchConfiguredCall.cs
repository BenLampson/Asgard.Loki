namespace Asgard.Loki.LLM.NewAPI.Internal;

/// <summary>一次原生搜索的不可变线路快照，不解释返回字节、SSE 或资料内容。</summary>
internal sealed class NewApiSearchConfiguredCall
{
    private static readonly string[] AuthenticationHeaders = ["Authorization", "api-key", "x-api-key", "x-goog-api-key", "x-amz-date", "x-amz-security-token", "x-amz-content-sha256", "Proxy-Authorization", "Cookie"];
    private readonly string? _header;
    private readonly string? _value;
    private readonly string _endpoint;
    private readonly string _host;
    private readonly HttpContent _content;
    internal HttpRequestMessage Request { get; }
    internal NewApiSearchConfiguredCall(byte[] body, NewApiSearchOptions options, string key)
    {
        var transport = options.Transport ?? new(); _value = CredentialValue(options, key);
        _header = options.Authentication switch { NewApiSearchAuthentication.Bearer => "Authorization", NewApiSearchAuthentication.Header => transport.CredentialName, _ => null };
        var endpoint = NewApiSearchConfiguredEndpoint.Create(options.BaseAddress, transport.SearchPath, transport, _value, options.Authentication); _endpoint = endpoint.OriginalString; _host = endpoint.Authority;
        Request = new(HttpMethod.Post, endpoint); _content = new ByteArrayContent(body.ToArray());
        try
        {
            Request.Content = _content; _content.Headers.ContentType = new("application/json"); Request.Headers.Host = _host; Request.Headers.Accept.ParseAdd("application/json");
            if (_header is not null && !Request.Headers.TryAddWithoutValidation(_header, _value)) throw new ArgumentException("搜索认证名称不是有效请求头。");
        }
        catch { Request.Dispose(); throw; }
    }
    internal static string? CredentialValue(NewApiSearchOptions options, string key)
    {
        if (options.Authentication == NewApiSearchAuthentication.None) return null;
        NewApiSearchConfiguredEndpoint.Text(key); if (options.Authentication != NewApiSearchAuthentication.Query && key.Any(c => c is < ' ' or > '~')) throw new ArgumentException("搜索认证头需要可打印 ASCII 密钥。");
        if (options.Authentication == NewApiSearchAuthentication.Bearer && key.Any(char.IsWhiteSpace)) throw new ArgumentException("搜索 Bearer 密钥不能包含空白。");
        var transport = options.Transport ?? new(); var value = options.Authentication == NewApiSearchAuthentication.Bearer ? "Bearer "+key : transport.CredentialPrefix+key+transport.CredentialSuffix; NewApiSearchConfiguredEndpoint.Text(value);
        if (options.Authentication == NewApiSearchAuthentication.Header && value.Any(c => c > '~')) throw new ArgumentException("搜索认证头需要可打印 ASCII 值。"); return value;
    }
    internal void BeforeSend(HttpClient client)
    {
        foreach (var name in AuthenticationHeaders) if (!name.Equals(_header, StringComparison.OrdinalIgnoreCase) && client.DefaultRequestHeaders.Contains(name)) throw new InvalidOperationException("搜索客户端包含未选择的认证或签名默认头。"); Verify(null);
    }
    internal void Verify(HttpResponseMessage? response)
    {
        if (Request.Method != HttpMethod.Post || Request.RequestUri?.OriginalString != _endpoint || !Headers(Request)
            || response?.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != _endpoint || !Headers(actual))
            || !ReferenceEquals(Request.Content, _content) || _content.Headers.ContentType?.ToString() != "application/json") throw new InvalidDataException("搜索实际目标、方法、认证或正文改变。");
    }
    private bool Headers(HttpRequestMessage request)
    {
        if (request.Headers.Host != _host || request.Headers.Accept.Count != 1 || request.Headers.Accept.Single().MediaType != "application/json") return false;
        foreach (var name in AuthenticationHeaders) if (!name.Equals(_header, StringComparison.OrdinalIgnoreCase) && request.Headers.Contains(name)) return false;
        return _header is null || request.Headers.NonValidated.TryGetValues(_header, out var values) && values.SequenceEqual([_value!]);
    }
}
