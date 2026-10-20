using System.Text.Json;

namespace Asgard.Loki.Video;

/// <summary>保留供应商完整任务窗口，不跨供应商重写媒体或元数据。</summary>
public sealed class VideoTaskSnapshot
{
    /// <summary>保存原生任务身份、原生状态和独立 JSON 快照。</summary>
    public VideoTaskSnapshot(string taskId, string? nativeStatus, VideoTaskState state, JsonElement data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("任务结果必须是原生 JSON 对象。", nameof(data));
        TaskId = taskId; NativeStatus = nativeStatus; State = state; Data = data.Clone();
    }
    /// <summary>供应商的原始任务 ID。</summary>
    public string TaskId { get; }
    /// <summary>原生状态文本；提交确认未返回状态时为 null。</summary>
    public string? NativeStatus { get; }
    /// <summary>供应商适配器明确识别的生命周期，未知值不折叠成成功。</summary>
    public VideoTaskState State { get; }
    /// <summary>完整原生 JSON，包括结果地址、错误、用量、实际版本和未知字段。</summary>
    public JsonElement Data { get; }
    /// <summary>只按明确已知状态判断终止，HTTP 成功或未知值不构成终态。</summary>
    public bool IsTerminal => State is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Cancelled or VideoTaskState.Expired;
}
