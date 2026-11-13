namespace Asgard.Loki.Image;

/// <summary>只选择一个完整图片上游，不执行调用、参数过滤或故障转移。</summary>
public interface ILokiImageProviderSelector
{
    /// <summary>返回候选集合中唯一一个目标的索引。</summary>
    int Select(string model, IReadOnlyList<LokiImageUpstreamProvider> providers);
}
