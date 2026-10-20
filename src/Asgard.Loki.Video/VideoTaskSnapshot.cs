using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asgard.Loki.Video;

/// <summary>保留供应商完整任务窗口，不跨供应商重写媒体或元数据。</summary>
public sealed class VideoTaskSnapshot
{
    /// <summary>保存原生任务身份、原生状态和独立 JSON 快照。</summary>
    public VideoTaskSnapshot(string taskId, string? nativeStatus, VideoTaskState state, JsonElement data)
        : this(taskId, nativeStatus, state, data, null) { }
    /// <summary>保留可选 HTTP 窗口；只允许空正文 202 的运行中查询明确没有原生 JSON。</summary>
    [JsonConstructor]
    public VideoTaskSnapshot(string taskId, string? nativeStatus, VideoTaskState state, JsonElement data, VideoTaskHttpResponse? httpResponse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        var absentJson = data.ValueKind == JsonValueKind.Undefined && nativeStatus is null
            && state == VideoTaskState.Running && httpResponse is { StatusCode: HttpStatusCode.Accepted, IsEmpty: true };
        if (data.ValueKind != JsonValueKind.Object && !absentJson) throw new ArgumentException("任务结果必须是原生 JSON 对象；只有明确空正文 202 的运行中窗口可以没有 JSON。", nameof(data));
        TaskId = taskId; NativeStatus = nativeStatus; State = state; Data = absentJson ? default : data.Clone(); HttpResponse = httpResponse;
    }
    /// <summary>供应商的原始任务 ID。</summary>
    public string TaskId { get; }
    /// <summary>原生状态文本；提交确认未返回状态时为 null。</summary>
    public string? NativeStatus { get; }
    /// <summary>供应商适配器明确识别的生命周期，未知值不折叠成成功。</summary>
    public VideoTaskState State { get; }
    /// <summary>完整原生 JSON；明确没有 JSON 的空正文 202 窗口为 Undefined，不补造对象或状态字段。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Data { get; }
    /// <summary>是否确实具有原生 JSON 对象，调用 Data 的对象操作前可检查。</summary>
    public bool HasJsonData => Data.ValueKind == JsonValueKind.Object;
    /// <summary>供应商显式保留的原始 HTTP 窗口；旧 JSON 构造路径保持 null。</summary>
    public VideoTaskHttpResponse? HttpResponse { get; }
    /// <summary>只按明确已知状态判断终止，HTTP 成功或未知值不构成终态。</summary>
    public bool IsTerminal => State is VideoTaskState.Succeeded or VideoTaskState.Failed or VideoTaskState.Cancelled or VideoTaskState.Expired;
}
