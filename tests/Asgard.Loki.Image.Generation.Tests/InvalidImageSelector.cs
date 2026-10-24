namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>返回候选集合之外的索引，验证客户端不容忍错误选择器。</summary>
internal sealed class InvalidImageSelector : ILokiImageProviderSelector
{
    public int Select(string model, IReadOnlyList<LokiImageUpstreamProvider> providers) => providers.Count;
}
