namespace Asgard.Loki.Image;

/// <summary>显式 multipart 图像编辑；图片列表与遮罩保持各自角色，不暗中转成 JSON 引用。</summary>
public sealed record ImageMultipartEditRequest
{
    /// <summary>公开编辑模型路由。</summary>
    public required string Model { get; init; }
    /// <summary>原始编辑提示词。</summary>
    public required string Prompt { get; init; }
    /// <summary>至少一个独立借用流，按调用者原始顺序上传。</summary>
    public required IReadOnlyList<ImageUpload> Images { get; init; }
    /// <summary>可选且独立的遮罩上传；核心不修改图像/遮罩的尺寸、像素或关联。</summary>
    public ImageUpload? Mask { get; init; }
    /// <summary>原生扩展文本字段，数组/嵌套字段名不自动展开或改名。</summary>
    public IReadOnlyList<ImageMultipartField> Fields { get; init; } = [];
}
