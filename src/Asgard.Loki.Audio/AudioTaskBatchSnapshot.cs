using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>供应商一次原生批量查询的返回项及完整信封；缺失任务不补造窗口。</summary>
public sealed class AudioTaskBatchSnapshot
{
    /// <summary>固定返回项及 JSON 所有权，保留供应商原始项顺序。</summary>
    public AudioTaskBatchSnapshot(IReadOnlyList<AudioTaskSnapshot> tasks, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(tasks); var snapshot = tasks.ToArray();
        foreach (var task in snapshot) ArgumentNullException.ThrowIfNull(task);
        if (data.ValueKind != JsonValueKind.Object) throw new ArgumentException("批量音频结果必须保留原生对象信封。");
        Internal.AudioTaskJson.Validate(data); Tasks = Array.AsReadOnly(snapshot); Data = data.Clone();
    }
    /// <summary>实际返回的任务窗口，可以少于请求数；关联校验由任务客户端负责。</summary>
    public IReadOnlyList<AudioTaskSnapshot> Tasks { get; }
    /// <summary>完整批量原生对象，原始数组顺序和未知信封字段不改变。</summary>
    public JsonElement Data { get; }
}
