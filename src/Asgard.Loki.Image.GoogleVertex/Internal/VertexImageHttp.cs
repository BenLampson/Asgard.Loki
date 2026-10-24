using System.Net.Http.Headers;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.GoogleVertex.Internal;

/// <summary>固定云资源、显式认证身份和每次 HTTP 所有权；不读取凭据文件、不登录、不重试。</summary>
internal sealed class VertexImageHttp
{
    private readonly IHttpClientFactory _factory;
    private readonly IGoogleVertexImageTokenSource? _source;
    private readonly string? _sourceIdentity;
    private readonly string _escapedResource;
    private readonly TimeProvider _time;
    internal GoogleVertexImageOptions Options { get; }
    internal VertexImageHttp(IHttpClientFactory factory, GoogleVertexImageOptions options, IGoogleVertexImageTokenSource? source = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.ModelResource, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); VertexImageJson.Text(value); }
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme != "https" || options.BaseAddress.Query.Length != 0
            || options.BaseAddress.Fragment.Length != 0 || options.BaseAddress.UserInfo.Length != 0 || options.BaseAddress.AbsolutePath.Contains('%'))
            throw new ArgumentException("Vertex 图片根地址必须为无凭据、查询、转义路径和片段的 HTTPS 地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        var parts = options.ModelResource.Split('/');
        var full = parts.Length == 8 && parts[0] == "projects" && parts[2] == "locations" && parts[4] == "publishers" && parts[5] == "google" && parts[6] == "models";
        var express = parts.Length == 4 && parts[0] == "publishers" && parts[1] == "google" && parts[2] == "models";
        if (!full && !(source is null && express)) throw new ArgumentException("Vertex 图片要求完整 Google 模型资源；API Key 入口也支持明确 Express 短资源。");
        foreach (var part in parts) Segment(part); _escapedResource = string.Join('/', parts.Select(Uri.EscapeDataString));
        if (options.QuotaProjectId is { } quota) Segment(quota);
        if (source is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); VertexImageJson.Text(options.ApiKey);
            if (options.ApiKey.Any(c => c is < '!' or > '~') || options.PrincipalIdentity is not null) throw new ArgumentException("API Key 入口需要独立原生密钥且不能携带令牌主体。");
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PrincipalIdentity); VertexImageJson.Text(options.PrincipalIdentity);
            if (options.ApiKey != "") throw new ArgumentException("令牌入口不能混入 API Key。");
            var identity = source.BindingIdentity; ArgumentException.ThrowIfNullOrWhiteSpace(identity); VertexImageJson.Text(identity); _sourceIdentity = identity;
        }
        _factory = factory; Options = options; _source = source; _time = time ?? TimeProvider.System;
    }
    /// <summary>每次操作只请求一个同主体令牌；短期轮换不改变绑定身份。</summary>
    internal async ValueTask<GoogleVertexImageAccessToken?> AuthenticateAsync(CancellationToken token)
    {
        Check(token); if (_source is null) return null;
        var access = await _source.GetTokenAsync(Options.PrincipalIdentity!, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Vertex 图片认证源未返回令牌。");
        if (access.PrincipalIdentity != Options.PrincipalIdentity) throw new InvalidOperationException("Vertex 图片认证主体不符。");
        Check(token, access); return access;
    }
    /// <summary>复核来源与取消；提供本次令牌时仅在发送之前核对有效期，不自动刷新或切换。</summary>
    internal void Check(CancellationToken token, GoogleVertexImageAccessToken? access = null)
    {
        token.ThrowIfCancellationRequested();
        if (_source is not null && _source.BindingIdentity != _sourceIdentity) throw new InvalidOperationException("Vertex 图片认证来源配置已变化，应注册新目标。");
        if (access is not null && access.ExpiresOn <= _time.GetUtcNow()) throw new InvalidOperationException("Vertex 图片访问令牌已过期。");
    }
    /// <summary>原生路径由完整资源及显式调用方法确定，Key 不进入查询。</summary>
    internal Uri Address(bool stream) => new(new Uri(Options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"),
        _escapedResource + (stream ? ":streamGenerateContent?alt=sse" : ":generateContent"));
    /// <summary>结果元数据仅包含公共身份和固定云资源，不含凭据。</summary>
    internal ImageResultMetadata Metadata(string model) => new(model, Options.ModelResource, Options.Id);
    /// <summary>构造本次拥有的原生 JSON 请求，按构造入口选择单一认证头。</summary>
    internal HttpRequestMessage Message(byte[] body, bool streaming, GoogleVertexImageAccessToken? access)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Address(streaming));
        try
        {
            if (_source is null) message.Headers.Add("x-goog-api-key", Options.ApiKey);
            else message.Headers.Authorization = new("Bearer", access?.AccessToken ?? throw new InvalidOperationException("Vertex 图片缺少本次令牌。"));
            if (Options.QuotaProjectId is { } quota) message.Headers.Add("x-goog-user-project", quota);
            message.Headers.Accept.ParseAdd(streaming ? "text/event-stream" : "application/json");
            message.Content = new ByteArrayContent(body); message.Content.Headers.ContentType = new("application/json"); return message;
        }
        catch { message.Dispose(); throw; }
    }
    /// <summary>借用工厂创建本次拥有的客户端，发送前复核认证和默认头。</summary>
    internal HttpClient Client(CancellationToken token, GoogleVertexImageAccessToken? access)
    {
        var client = _factory.CreateClient(Options.HttpClientName);
        try
        {
            Check(token, access);
            if (client.DefaultRequestHeaders.Authorization is not null || new[] { "x-goog-api-key", "api-key", "x-api-key", "x-goog-user-project" }.Any(client.DefaultRequestHeaders.Contains))
                throw new InvalidOperationException("Vertex 图片默认 HTTP 头携带未绑定的认证或项目身份。");
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    /// <summary>拒绝实际响应的方法或地址偏离完整目标。</summary>
    internal static void Validate(HttpResponseMessage response, HttpRequestMessage request)
    {
        if (response.RequestMessage is { } actual && (actual.Method != request.Method || actual.RequestUri != request.RequestUri)) throw new InvalidDataException("Vertex 图片实际响应目标/方法与固定目标不符。");
    }
    /// <summary>完整原生响应经过所有权释放和来源校验之后再返回。</summary>
    internal async Task<JsonElement> SendAsync(byte[] body, CancellationToken token)
        => (await SendRawAsync(body, token).ConfigureAwait(false)).Data;
    /// <summary>同时保留原始字节，不通过 JSON 重序列化制造原始响应。</summary>
    internal async Task<(JsonElement Data, ImageRawResponse Raw)> SendRawAsync(byte[] body, CancellationToken token)
    {
        var access = await AuthenticateAsync(token).ConfigureAwait(false); byte[] bytes; int status; string? type;
        using (var request = Message(body, false, access))
        using (var client = Client(token, access))
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); Validate(response, request);
            bytes = await HttpBodyReader.ReadAsync(response.Content, Options.MaxResponseBytes, token).ConfigureAwait(false);
            status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString();
        }
        Check(token); return (Result(bytes, status, type), new(status, bytes, type));
    }
    /// <summary>保留实际 HTTP/API 错误字节，仅成功正文进入原生 JSON 校验。</summary>
    internal static JsonElement Result(byte[] bytes, int status, string? type)
    {
        if (status is < 200 or >= 300) throw new ImageUpstreamException("Vertex 图片 HTTP 调用失败。", status, bytes, type);
        if (type is not null && (!MediaTypeHeaderValue.TryParse(type, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || header.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Vertex 完整图片结果必须为 UTF-8 JSON。");
        var root = VertexImageJson.Parse(bytes);
        if (VertexImageJson.Error(root)) throw new ImageUpstreamException("Vertex 图片原生 API 错误。", status, bytes, type);
        return root;
    }
    /// <summary>资源路径段拒绝跳转、查询和已转义路径。</summary>
    private static void Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); VertexImageJson.Text(value);
        if (value is "." or ".." || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/\\%?#:".Contains(c))) throw new ArgumentException("Vertex 图片资源路径段无效。");
    }
}
