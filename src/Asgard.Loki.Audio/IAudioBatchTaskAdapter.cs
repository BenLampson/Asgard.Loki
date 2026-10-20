namespace Asgard.Loki.Audio;

/// <summary>可选的原生批量任务查询，不能用多次单条查询冒充一个批量调用。</summary>
public interface IAudioBatchTaskAdapter : IAudioTaskAdapter
{
    /// <summary>对同一完整目标显式批量查询一次；只返回上游确实返回的项。</summary>
    Task<AudioTaskBatchSnapshot> GetBatchAsync(IReadOnlyList<AudioTaskQuery> queries, CancellationToken cancellationToken = default);
}
