using System.Text.Json;

namespace Asgard.Loki.Image.Replicate;

/// <summary>单次模型图像输入上传的原生确认，不提供文件目录、读取、删除或存储服务。</summary>
public sealed class ReplicateImageInput
{
    internal ReplicateImageInput(JsonElement data, string url) { Data = data.Clone(); Url = url; }
    /// <summary>完整原生上传元数据，不丢弃 ID、校验、大小、到期或扩展字段。</summary>
    public JsonElement Data { get; }
    /// <summary>原生 urls.get，可由调用者明确放入模型输入；不会自动下载或调用预测。</summary>
    public string Url { get; }
}
