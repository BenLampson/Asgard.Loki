namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>单次请求中的 custom 名称映射，不通过名称前缀猜测原始工具。</summary>
internal sealed record CustomToolBinding
{
    /// <summary>构造不可变的原始名称与编码名称。</summary>
    internal CustomToolBinding(string originalName, string encodedName) { OriginalName = originalName; EncodedName = encodedName; }
    /// <summary>原始 custom 工具名称。</summary>
    internal string OriginalName { get; }
    /// <summary>本次请求独占的普通函数名称。</summary>
    internal string EncodedName { get; }
}
