using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>一次已绑定批量查询；实际返回与缺失引用分开保留，不生成虚构状态。</summary>
public sealed class AudioTaskBatchResult
{
    internal AudioTaskBatchResult(IReadOnlyList<AudioTask> tasks, IReadOnlyList<AudioTaskReference> missing, JsonElement data)
    { Tasks = Array.AsReadOnly(tasks.ToArray()); MissingReferences = Array.AsReadOnly(missing.ToArray()); Data = data.Clone(); }
    /// <summary>实际返回的任务，按调用者输入引用顺序排列。</summary>
    public IReadOnlyList<AudioTask> Tasks { get; }
    /// <summary>上游未返回的已请求引用，不等价于失败或终态。</summary>
    public IReadOnlyList<AudioTaskReference> MissingReferences { get; }
    /// <summary>原始完整批量信封，含供应商自己的原始数组顺序。</summary>
    public JsonElement Data { get; }
}
