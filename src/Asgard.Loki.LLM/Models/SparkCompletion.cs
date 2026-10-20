namespace Asgard.Loki.LLM;

/// <summary>一次完整星火调用的有序原生消息窗口，不拼接文本、不累计用量或补造 JSON 响应。</summary>
public sealed class SparkCompletion
{
    /// <summary>复制消息集合所有权，元素自身已经持有不可变 JSON；禁止混合不同目标元数据。</summary>
    public SparkCompletion(IEnumerable<SparkEvent> events, LlmResultMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(events); ArgumentNullException.ThrowIfNull(metadata); var copies = events.ToArray();
        if (copies.Length == 0 || copies.Any(item => item is null || item.Metadata != metadata)) throw new ArgumentException("星火完整消息窗口必须非空且属于同一完整目标。", nameof(events));
        Events = Array.AsReadOnly(copies); Metadata = metadata;
    }
    /// <summary>完整原生消息序列，包含插件、全部文本项、用量和未知字段，不表示合成的单条响应。</summary>
    public IReadOnlyList<SparkEvent> Events { get; }
    /// <summary>与原生 header.sid 和正文分开的 SDK 完整路由元数据。</summary>
    public LlmResultMetadata Metadata { get; }
}
