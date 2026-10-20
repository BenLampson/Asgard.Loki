using System.Net.Http.Headers;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>单次明确账户和目标的拥有请求；与来源/短期凭据一同验证实际线协议。</summary>
internal sealed class CodexHttpCall
{
    private readonly CodexAuthentication _auth;
    private readonly CodexAccessToken _access;
    private readonly string _principal;
    private readonly string _endpoint;
    private readonly HttpContent _content;
    private readonly string _accept;
    internal HttpRequestMessage Request { get; }
    /// <summary>只设置选中账户/令牌、明确可选头及精确 application/json，不包含 charset。</summary>
    internal CodexHttpCall(CodexAuthentication auth, CodexAccessToken access, string principal, Uri endpoint, byte[] body, bool stream)
    {
        _auth = auth; _access = access; _principal = principal; _endpoint = endpoint.OriginalString; _accept = stream ? "text/event-stream" : "application/json";
        Request = new(HttpMethod.Post, endpoint); _content = new ByteArrayContent(body);
        try
        {
            Request.Content = _content; _content.Headers.ContentType = new("application/json");
            Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken); Request.Headers.Add("chatgpt-account-id", auth.Account.AccountId);
            Request.Headers.Accept.ParseAdd(_accept);
            if (auth.Account.Beta is { } beta) Request.Headers.Add("OpenAI-Beta", beta);
            if (auth.Account.Originator is { } originator) Request.Headers.Add("originator", originator);
        }
        catch { Request.Dispose(); throw; }
    }
    /// <summary>默认头不能补进未选认证或未绑定 Beta/originator，显式同名头由当前请求拥有。</summary>
    internal void BeforeSend(HttpClient client)
    {
        if (new[] { "api-key", "x-api-key", "x-goog-api-key" }.Any(client.DefaultRequestHeaders.Contains)
            || _auth.Account.Beta is null && client.DefaultRequestHeaders.Contains("OpenAI-Beta")
            || _auth.Account.Originator is null && client.DefaultRequestHeaders.Contains("originator")) throw new InvalidOperationException("Codex 工厂包含未绑定认证或原生配置头。");
        Verify(null);
    }
    /// <summary>资源关闭后仍检查原始请求与实际响应地址，防止处理器重定向、换身份或改传输。</summary>
    internal void Verify(HttpResponseMessage? response)
    {
        _auth.Validate(_principal, _access);
        if (Request.Method != HttpMethod.Post || Request.RequestUri?.OriginalString != _endpoint
            || response?.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri?.OriginalString != _endpoint)
            || Request.Headers.Accept.Count != 1 || Request.Headers.Accept.Single().MediaType != _accept
            || !ReferenceEquals(Request.Content, _content) || _content.Headers.ContentType?.ToString() != "application/json"
            || Request.Headers.Authorization?.Scheme != "Bearer" || Request.Headers.Authorization.Parameter != _access.AccessToken
            || !Same("chatgpt-account-id", _auth.Account.AccountId) || !Same("OpenAI-Beta", _auth.Account.Beta) || !Same("originator", _auth.Account.Originator))
            throw new InvalidDataException("Codex 原生方法、完整目标、账户、令牌或配置头发生变化。");
    }
    /// <summary>保留原始头值，不接受重复关联或隐式默认配置。</summary>
    private bool Same(string name, string? expected)
    { return Request.Headers.TryGetValues(name, out var values) ? expected is not null && values.SequenceEqual([expected]) : expected is null; }
}
