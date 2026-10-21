using System.Text.Json;

namespace Asgard.Loki.Audio.SunoAPI;

/// <summary>原生批量状态接口没有返回指定任务，不补造未知状态或成功结果。</summary>
public sealed class SunoAudioTaskMissingException : InvalidOperationException
{
    /// <summary>保留查询 ID 与完整原生空结果信封。</summary>
    public SunoAudioTaskMissingException(string taskId, JsonElement response) : base("Suno-API 状态查询未返回指定任务。")
    { TaskId = taskId; Response = response.Clone(); }
    /// <summary>未返回的原生任务 ID。</summary>
    public string TaskId { get; }
    /// <summary>完整原生响应，不插入缺失任务。</summary>
    public JsonElement Response { get; }
}
