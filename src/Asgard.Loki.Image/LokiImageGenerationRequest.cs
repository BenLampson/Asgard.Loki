using System.Text.Json.Nodes;

namespace Asgard.Loki.Image;

/// <summary>供应商无关的图片生成需求；未设置的可选参数不会发送或注入默认值。</summary>
public sealed record LokiImageGenerationRequest
{
    /// <summary>注册的公开模型名。</summary>
    public required string Model { get; init; }
    /// <summary>原始提示词。</summary>
    public required string Prompt { get; init; }
    /// <summary>精确请求的图片数量，不表示最多数量。</summary>
    public int? Count { get; init; }
    /// <summary>精确的像素尺寸。</summary>
    public LokiImageSize? Size { get; init; }
    /// <summary>供应商声明支持的画面比例，例如 16:9。</summary>
    public string? AspectRatio { get; init; }
    /// <summary>目标声明的质量值，不与像素尺寸混用。</summary>
    public string? Quality { get; init; }
    /// <summary>图片编码，例如 png、jpeg 或 webp，不是 URL/Base64 传输形式。</summary>
    public string? OutputFormat { get; init; }
    /// <summary>目标声明的背景模式，例如 transparent。</summary>
    public string? Background { get; init; }
    /// <summary>原生随机种子。</summary>
    public long? Seed { get; init; }
    /// <summary>负向提示词。</summary>
    public string? NegativePrompt { get; init; }
    /// <summary>原生推理步数。</summary>
    public int? Steps { get; init; }
    /// <summary>原生引导系数。</summary>
    public double? GuidanceScale { get; init; }
    /// <summary>原生水印开关。</summary>
    public bool? Watermark { get; init; }
    /// <summary>原生扩展字段；不得覆盖统一参数映射或固定模型。</summary>
    public JsonObject NativeParameters { get; init; } = new();
}
