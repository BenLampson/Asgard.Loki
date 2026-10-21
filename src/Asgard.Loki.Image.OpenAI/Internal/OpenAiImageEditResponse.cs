namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>编辑原生容器使用自己的 HTTP/业务错误契约，不把生成异常套在编辑上。</summary>
internal static class OpenAiImageEditResponse
{
    /// <summary>共用纯容器校验，保留编辑专用的完整原始失败字节。</summary>
    internal static ImageGenerationResult Result(HttpResponseMessage response, byte[] body, ImageResultMetadata metadata)
        => ImageNativeJson.Result(response, body, metadata, () => new ImageEditUpstreamException(
            "OpenAI 图像编辑返回了原生失败。", (int)response.StatusCode, body, response.Content.Headers.ContentType?.ToString()));
}
