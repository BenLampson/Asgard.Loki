using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>可选原生任务结果视图，独立于状态查询且不隐式轮询/下载。</summary>
public interface IAudioTaskResultAdapter : IAudioTaskAdapter
{
    /// <summary>读取一次原生结果对象；载荷不被解释为成功终态。</summary>
    Task<JsonElement> GetResultAsync(AudioTaskQuery query, CancellationToken cancellationToken = default);
}
