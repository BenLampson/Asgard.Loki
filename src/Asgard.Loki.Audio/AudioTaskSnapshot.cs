using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>供应商原生任务窗口与明确的 SDK 上下文；不自动查询或构造媒体结果。</summary>
public sealed class AudioTaskSnapshot
{
    /// <summary>复制完整原生对象；操作名是关联上下文，不注入原生 JSON。</summary>
    public AudioTaskSnapshot(string taskId, string operation, string? nativeStatus, AudioTaskState state, JsonElement data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        Internal.AudioTaskJson.Text(taskId); Internal.AudioTaskJson.Text(operation);
        if (nativeStatus is not null) Internal.AudioTaskJson.Text(nativeStatus);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("音频任务窗口必须是原生 JSON 对象。");
        Internal.AudioTaskJson.Validate(data); TaskId = taskId; Operation = operation; NativeStatus = nativeStatus; State = state; Data = data.Clone();
    }
    /// <summary>原生任务 ID。</summary>
    public string TaskId { get; }
    /// <summary>本次关联的原生操作，不保证上游每个窗口都返回操作字段。</summary>
    public string Operation { get; }
    /// <summary>原生状态文本，提交确认没有状态时为空。</summary>
    public string? NativeStatus { get; }
    /// <summary>供应商明确识别的生命周期。</summary>
    public AudioTaskState State { get; }
    /// <summary>完整原生 JSON 对象，保留原文而不填造状态/媒体/用量字段。</summary>
    public JsonElement Data { get; }
    /// <summary>只依据供应商明确识别的终态，未知/提交状态不是完成。</summary>
    public bool IsTerminal => State is AudioTaskState.Succeeded or AudioTaskState.Failed or AudioTaskState.Cancelled or AudioTaskState.Expired;
}
