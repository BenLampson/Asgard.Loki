namespace Asgard.Loki.Audio;

/// <summary>文件转录/翻译的公共模型路由、借用上传和原生表单文本，不定义供应商字段统一方言。</summary>
public sealed record AudioFileRequest
{
    /// <summary>调用者配置的公开路由名，实际模型由所选完整适配目标绑定。</summary>
    public required string Model { get; init; }
    /// <summary>必需的单次主音频上传，不复制或读取流内容。</summary>
    public required AudioUpload File { get; init; }
    /// <summary>原生文本字段；数组/嵌套配置使用供应商原生名称及文本，不隐式展开 JSON。</summary>
    public IReadOnlyList<AudioMultipartField> Fields { get; init; } = [];
}
