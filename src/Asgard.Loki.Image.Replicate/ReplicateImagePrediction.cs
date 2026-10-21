using System.Text.Json;
using Asgard.Loki.Image.Replicate.Internal;

namespace Asgard.Loki.Image.Replicate;

/// <summary>单次原生预测快照，HTTP 等待结束不代表预测已成功完成。</summary>
public sealed class ReplicateImagePrediction
{
    private readonly byte[] _body;
    private readonly int _statusCode;
    private readonly string? _contentType;
    internal ReplicateImagePrediction(JsonElement data, ImageResultMetadata metadata, byte[] body, int statusCode, string? contentType)
    {
        Data = data.Clone(); Metadata = metadata; _body = body.ToArray(); _statusCode = statusCode; _contentType = contentType;
        Id = ReplicateImageJson.RequiredText(Data, "id"); Status = ReplicateImageJson.RequiredText(Data, "status");
        Images = ReplicateImageJson.Images(Data);
    }
    /// <summary>完整原生输入、输出、状态、失败、时间、日志、指标、URL 与未知字段。</summary>
    public JsonElement Data { get; }
    /// <summary>公共模型、实际资源与固定目标元数据。</summary>
    public ImageResultMetadata Metadata { get; }
    /// <summary>原生预测 ID，不改为本地任务号。</summary>
    public string Id { get; }
    /// <summary>原生大小写状态，不推断未知状态。</summary>
    public string Status { get; }
    /// <summary>仅已知 succeeded、failed、canceled 为终态。</summary>
    public bool IsTerminal => Status is "succeeded" or "failed" or "canceled";
    /// <summary>只有明确 succeeded 且原生 error 为空才表示成功。</summary>
    public bool IsSucceeded => Status == "succeeded" && (!Data.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null);
    /// <summary>已返回的原生字符串图片视图，可能是处理中或失败时的部分结果。</summary>
    public IReadOnlyList<GeneratedImage> Images { get; }
    /// <summary>显式提取完整成功结果；未完成或失败保留原始 HTTP 状态和完整正文，不轮询。</summary>
    public ImageGenerationResult AsCompletedResult()
    {
        if (!IsSucceeded) throw new ImageUpstreamException("Replicate 预测尚未成功完成，请检查完整原生状态与错误。", _statusCode, _body, _contentType);
        return new(Data, Metadata, Images);
    }
}
