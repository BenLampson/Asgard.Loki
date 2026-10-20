namespace Asgard.Loki.Audio.Cloudflare;

/// <summary>显式选择原生输入结构，不根据模型名猜测或自动切换。</summary>
public enum CloudflareAudioInputFormat
{
    /// <summary>原始文件字节，只有识别调用，不携带额外参数或翻译任务标记。</summary>
    RawBinary,
    /// <summary>原生 JSON audio Base64 与显式 task，适用于支持该结构的上游模型。</summary>
    JsonBase64
}
