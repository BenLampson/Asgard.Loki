using System.Text.Json;

namespace Asgard.Loki.Image;

/// <summary>统一图片生成视图，同时保留未改写的原生结果和可用的原始 HTTP 响应。</summary>
public sealed class LokiImageGenerationResult
{
    /// <summary>保存已完成的一次原生生成结果，不把空列表补造成图片。</summary>
    public LokiImageGenerationResult(ImageGenerationResult nativeResult, LokiImageGenerationState? state = null,
        string? taskId = null, JsonElement? nativeTaskReference = null)
    {
        ArgumentNullException.ThrowIfNull(nativeResult); NativeResult = nativeResult;
        State = state ?? InferState(nativeResult); TaskId = taskId; NativeTaskReference = nativeTaskReference?.Clone();
    }
    /// <summary>原有完整结果。</summary>
    public ImageGenerationResult NativeResult { get; }
    /// <summary>原始 JSON。</summary>
    public JsonElement Data => NativeResult.Data;
    /// <summary>供应商提供的原始 HTTP 响应，自定义 Handler 未提供时为空。</summary>
    public ImageRawResponse? RawResponse => NativeResult.RawResponse;
    /// <summary>公开模型名。</summary>
    public string Model => NativeResult.Metadata.PublicModel;
    /// <summary>实际模型名。</summary>
    public string UpstreamModel => NativeResult.Metadata.UpstreamModel;
    /// <summary>选中的完整上游标识。</summary>
    public string ProviderId => NativeResult.Metadata.ProviderId;
    /// <summary>明确区分任务处理、部分成功、失败和无图片结果。</summary>
    public LokiImageGenerationState State { get; }
    /// <summary>异步上游原生任务 ID。</summary>
    public string? TaskId { get; }
    /// <summary>Handler 的原生任务引用快照，不包含明文凭据。</summary>
    public JsonElement? NativeTaskReference { get; }
    /// <summary>客户端生成的完整目标绑定引用，可序列化后用于显式查询。</summary>
    public LokiImageTaskReference? TaskReference { get; internal set; }
    /// <summary>按上游顺序保留的图片，包括原生图片级失败项。</summary>
    public IReadOnlyList<LokiGeneratedImage> Images => NativeResult.Images.Select(image => new LokiGeneratedImage(image)).ToArray();

    private static LokiImageGenerationState InferState(ImageGenerationResult result)
    {
        var images = result.Images.Select(image => new LokiGeneratedImage(image)).ToArray();
        if (images.Length == 0) return LokiImageGenerationState.NoImage;
        var succeeded = images.Count(image => image.Error is null
            && (image.NativeImage.Content is not null || !string.IsNullOrEmpty(image.Url) || !string.IsNullOrEmpty(image.Base64)));
        var failed = images.Count(image => image.Error is not null);
        if (succeeded == images.Length) return LokiImageGenerationState.Succeeded;
        if (failed == images.Length) return LokiImageGenerationState.Failed;
        if (succeeded > 0 && failed > 0) return LokiImageGenerationState.PartiallySucceeded;
        return LokiImageGenerationState.Unknown;
    }
}
