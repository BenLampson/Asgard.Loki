namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>工具身份按三个独立分量比较；根名称里的点号等字符不会伪装成 namespace。</summary>
internal sealed record ConvertedToolIdentity
{
    /// <summary>复制已经校验的源身份。</summary>
    internal ConvertedToolIdentity(string? toolNamespace, string name, bool custom) { Namespace = toolNamespace; Name = name; IsCustom = custom; }
    /// <summary>原始 namespace；null 表示根作用域，不能与空串混同。</summary>
    internal string? Namespace { get; }
    /// <summary>不作拼接、截断或规范化的原始名称。</summary>
    internal string Name { get; }
    /// <summary>custom 文本与普通 JSON 函数是两种不同身份。</summary>
    internal bool IsCustom { get; }
}
