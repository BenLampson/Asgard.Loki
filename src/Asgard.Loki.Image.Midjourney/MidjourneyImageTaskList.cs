using System.Text.Json;

namespace Asgard.Loki.Image.Midjourney;

/// <summary>一次按明确 ID 查询的原生任务数组，不按请求位置错误关联或补齐缺失任务。</summary>
public sealed class MidjourneyImageTaskList : MidjourneyImageResponse
{
    internal MidjourneyImageTaskList(JsonElement data, MidjourneyImageHttpResponse http, IReadOnlyList<MidjourneyImageTask> tasks) : base(data, http)
        => Tasks = Array.AsReadOnly(tasks.ToArray());
    /// <summary>按原生返回数组顺序保存，引用通过各项原生 id 关联；缺失项不伪造。</summary>
    public IReadOnlyList<MidjourneyImageTask> Tasks { get; }
}
