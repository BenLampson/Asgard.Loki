namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>单次请求中的工具身份映射，不通过名称前缀或分隔符猜测原始 namespace。</summary>
internal sealed record ConvertedToolBinding
{
    /// <summary>构造不可变的原始名称与编码名称。</summary>
    internal ConvertedToolBinding(ConvertedToolIdentity identity, string encodedName) { Identity = identity; EncodedName = encodedName; }
    /// <summary>完整原始身份，包括根作用域/namespace 与函数/custom 种类。</summary>
    internal ConvertedToolIdentity Identity { get; }
    /// <summary>本次请求独占的普通函数名称。</summary>
    internal string EncodedName { get; }
}
