namespace Asgard.Loki.Image.Ali;

/// <summary>明确的原生异步提交路径，不按模型名猜测或切换。</summary>
public enum AliImageTaskOperation
{
    /// <summary>现代 image-generation/generation。</summary>
    ImageGeneration,
    /// <summary>原生 text2image/image-synthesis。</summary>
    TextToImage,
    /// <summary>原生 image2image/image-synthesis。</summary>
    ImageToImage
}
