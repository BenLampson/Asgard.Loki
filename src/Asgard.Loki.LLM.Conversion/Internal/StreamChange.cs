namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>单次流内一个内容块的文本增量或完整函数调用。</summary>
internal sealed record StreamChange(int Index, string? Delta = null, bool Tool = false);
