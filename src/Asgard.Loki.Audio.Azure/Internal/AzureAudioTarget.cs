namespace Asgard.Loki.Audio.Azure.Internal;

/// <summary>同一音频能力内的固定 URI/身份值，不协调语音或文件业务流程。</summary>
internal sealed class AzureAudioTarget
{
    internal AzureAudioOptions Options { get; }
    internal AzureAudioTarget(AzureAudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var text in new[] { options.Id, options.Deployment, options.ApiVersion, options.ApiKey, options.HttpClientName })
        { ArgumentException.ThrowIfNullOrWhiteSpace(text); AzureAudioJson.Text(text); }
        if (options.BodyModel is { } model) { ArgumentException.ThrowIfNullOrWhiteSpace(model); AzureAudioJson.Text(model); }
        if (options.ApiKey.Any(char.IsControl) || options.ApiVersion.Any(char.IsControl)) throw new ArgumentException("Azure 音频身份或版本含控制字符。");
        if (options.Deployment is "." or ".." || options.Deployment.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\' or '%'))
            throw new ArgumentException("Azure 部署名必须是无歧义的单一路径段。");
        ArgumentNullException.ThrowIfNull(options.Endpoint);
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme is not ("https" or "http") || options.Endpoint.UserInfo.Length != 0
            || options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0) throw new ArgumentException("Azure 资源根地址必须为无凭据、查询参数和片段的 HTTP(S) 地址。");
        if (options.MaxRequestBytes <= 0 || options.MaxResponseBytes <= 0 || options.MaxAudioBytes <= 0 || options.MaxUploadBytes <= 0
            || options.ReadBufferSize is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options));
        Options = options;
    }
    /// <summary>只构造已实现的原生操作；版本是单独查询值，不拆分或追加调用者字符串。</summary>
    internal Uri Address(string operation)
    {
        if (operation is not ("speech" or "transcriptions" or "translations")) throw new ArgumentException("不支持的 Azure 音频操作。");
        return new(Options.Endpoint.AbsoluteUri.TrimEnd('/') + "/openai/deployments/" + Uri.EscapeDataString(Options.Deployment)
            + "/audio/" + operation + "?api-version=" + Uri.EscapeDataString(Options.ApiVersion));
    }
    /// <summary>认证头只来自本完整目标，不使用 Bearer 或 LLM 认证层。</summary>
    internal HttpRequestMessage Message(string operation)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Address(operation));
        try { message.Headers.Add("api-key", Options.ApiKey); return message; }
        catch { message.Dispose(); throw; }
    }
    /// <summary>拒绝外部处理器改变实际方法或固定地址后的成功结果。</summary>
    internal void ValidateResponse(HttpResponseMessage response, string operation)
    {
        if (response.RequestMessage is { } actual && (actual.Method != HttpMethod.Post || actual.RequestUri != Address(operation)))
            throw new InvalidDataException("Azure 音频响应目标或方法与固定配置不一致。");
    }
}
