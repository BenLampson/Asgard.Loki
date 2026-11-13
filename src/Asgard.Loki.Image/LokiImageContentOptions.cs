namespace Asgard.Loki.Image;

/// <summary>显式获取图片内容的独立传输边界，不转发生成凭据。</summary>
public sealed record LokiImageContentOptions
{
    /// <summary>应用提供的独立命名客户端，应禁用默认认证、自动重定向和重试。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.Image.Content";
    /// <summary>单张图片解码或下载的最大字节数。</summary>
    public int MaximumBytes { get; init; } = 64 * 1024 * 1024;
}
