using System.Net.Http.Headers;
using System.Text;
using Asgard.Loki.Image.OpenAI.Internal;

namespace Asgard.Loki.Image.OpenAI;

/// <summary>OpenAI 原生 JSON/multipart 图像编辑目标，不与生成/其他能力共享调用状态或隐式重试。</summary>
public sealed class OpenAiImageEditAdapter : IImageEditAdapter
{
    private readonly IHttpClientFactory _factory;
    private readonly OpenAiImageOptions _options;
    private readonly Uri _uri;
    /// <summary>独立声明完整编辑目标；复用图像配置类型，不隐式借用已注册生成目标。</summary>
    public OpenAiImageEditAdapter(IHttpClientFactory factory, OpenAiImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id); ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey); ArgumentException.ThrowIfNullOrWhiteSpace(options.HttpClientName);
        _ = new UTF8Encoding(false, true).GetByteCount(options.Model);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("http" or "https") || options.BaseAddress.UserInfo.Length != 0
            || options.BaseAddress.Query.Length != 0 || options.BaseAddress.Fragment.Length != 0) throw new ArgumentException("编辑基础地址必须是无凭据/查询/片段的 HTTP(S) 绝对地址。");
        _factory = factory; _options = options; _uri = new(new Uri(options.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), "images/edits");
    }
    /// <inheritdoc />
    public string Id => _options.Id;
    /// <inheritdoc />
    public Task<ImageGenerationResult> EditAsync(ImageEditRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = OpenAiImageEditPayload.Snapshot(request, cancellationToken);
        return SendAsync(OpenAiImageEditPayload.Json(snapshot, _options.Model), snapshot.Model, cancellationToken);
    }
    /// <inheritdoc />
    public Task<ImageGenerationResult> EditMultipartAsync(ImageMultipartEditRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = OpenAiImageEditPayload.Snapshot(request, cancellationToken);
        return SendAsync(OpenAiImageEditPayload.Multipart(snapshot, _options.Model), snapshot.Model, cancellationToken);
    }
    /// <summary>一次请求，结果在所有响应/客户端资源成功释放后才返回。</summary>
    private async Task<ImageGenerationResult> SendAsync(HttpContent content, string publicModel, CancellationToken token)
    {
        ImageGenerationResult result;
        using (content)
        using (var message = CreateMessage(content))
        using (var client = _factory.CreateClient(_options.HttpClientName))
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            OpenAiImageEditResponse.Headers(response);
            var body = await OpenAiImageEditResponse.ReadAsync(response, token).ConfigureAwait(false);
            result = OpenAiImageEditResponse.Result(response, body, new(publicModel, _options.Model, Id));
        }
        token.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>原生身份头来自固定配置，地址不受 JSON/表单扩展字段控制。</summary>
    private HttpRequestMessage CreateMessage(HttpContent content)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _uri);
        try
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            if (_options.Organization is not null) message.Headers.Add("OpenAI-Organization", _options.Organization);
            if (_options.Project is not null) message.Headers.Add("OpenAI-Project", _options.Project);
            message.Content = content; return message;
        }
        catch { message.Dispose(); throw; }
    }
}
