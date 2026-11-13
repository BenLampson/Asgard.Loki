using System.Text.Json;
using Asgard.Loki.Image.Ali.Internal;

namespace Asgard.Loki.Image.Ali;

/// <summary>保留原生状态、失败、时间、用量和部分产物的单次任务快照。</summary>
public sealed class AliImageTask
{
    internal AliImageTask(JsonElement data, AliImageTaskReference reference)
    { Data = data.Clone(); Reference = reference; }
    /// <summary>完整未改写的原生正文。</summary>
    public JsonElement Data { get; }
    /// <summary>单次提交或查询的原始 HTTP 响应。</summary>
    public ImageRawResponse? RawResponse { get; init; }
    /// <summary>完整目标绑定。</summary>
    public AliImageTaskReference Reference { get; }
    /// <summary>原生状态，不把未知状态猜测为成功。</summary>
    public string? Status => Data.GetProperty("output").TryGetProperty("task_status", out var value) ? value.GetString() : null;
    /// <summary>仅已知 SUCCEEDED、FAILED、CANCELED 为终态。</summary>
    public bool IsTerminal => Status is "SUCCEEDED" or "FAILED" or "CANCELED";
    /// <summary>原生已返回的图像引用，不下载，失败中的部分产物也保留。</summary>
    public IReadOnlyList<GeneratedImage> Images => AliImageJson.Images(Data);
}
