using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Image.Azure.Internal;

/// <summary>图像供应商内的固定 URI/认证及有界 HTTP 交换，不协调其他能力。</summary>
internal sealed class AzureImageTarget
{
    private readonly IHttpClientFactory _factory;
    /// <summary>不可变完整目标配置。</summary>
    internal AzureImageOptions Options { get; }
    /// <summary>注册时验证所有固定字段，不访问网络或其他项目配置。</summary>
    internal AzureImageTarget(IHttpClientFactory factory, AzureImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory); ArgumentNullException.ThrowIfNull(options);
        foreach (var value in new[] { options.Id, options.Deployment, options.ApiVersion, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); AzureImageJson.Text(value); }
        if (options.BodyModel is { } model) { ArgumentException.ThrowIfNullOrWhiteSpace(model); AzureImageJson.Text(model); }
        if (options.ApiKey.Any(value => value is < '!' or > '~') || options.ApiVersion.Any(char.IsControl))
            throw new ArgumentException("Azure 图像凭据或版本包含无效字符。");
        if (options.Deployment is "." or ".." || options.Deployment.Any(value => char.IsControl(value) || char.IsWhiteSpace(value) || value is '/' or '\\' or '%'))
            throw new ArgumentException("Azure 图像部署必须是无歧义的单一路径段。");
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("https" or "http") || options.Endpoint.UserInfo.Length != 0
            || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0 || options.Endpoint.AbsolutePath.Contains('%'))
            throw new ArgumentException("Azure 图像资源根地址必须为无凭据、查询、转义路径和片段的 HTTP(S) 绝对地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0 || options.MaxUploadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _factory = factory; Options = options;
    }
    /// <summary>版本仅作为一个转义查询值；部署原样保留，不按模型目录重写。</summary>
    private Uri Address(string operation)
    {
        if (operation is not ("generations" or "edits")) throw new ArgumentException("未知 Azure 图像操作。");
        return new(Options.Endpoint.AbsoluteUri.TrimEnd('/') + "/openai/deployments/" + Uri.EscapeDataString(Options.Deployment)
            + "/images/" + operation + "?api-version=" + Uri.EscapeDataString(Options.ApiVersion));
    }
    /// <summary>本次内容所有权移交消息；完整读取/资源释放前不交付任何成功结果。</summary>
    internal async Task<AzureImageResponse> SendAsync(string operation, HttpContent content, CancellationToken token)
    {
        using (content)
        using (var message = new HttpRequestMessage(HttpMethod.Post, Address(operation)))
        {
            message.Content = content; message.Headers.Add("api-key", Options.ApiKey); message.Headers.Accept.ParseAdd("application/json");
            token.ThrowIfCancellationRequested();
            using var client = _factory.CreateClient(Options.HttpClientName);
            client.DefaultRequestHeaders.Authorization = null; client.DefaultRequestHeaders.Remove("api-key");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != Address(operation)))
                throw new InvalidDataException("Azure 图像响应目标或方法与固定配置不一致。");
            var body = await HttpBodyReader.ReadAsync(response.Content, Options.MaxResponseBytes, token).ConfigureAwait(false);
            var result = new AzureImageResponse(response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
            return result;
        }
    }
}
