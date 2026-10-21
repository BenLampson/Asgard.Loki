namespace Asgard.Loki.LLM.Conversion;

/// <summary>调用者明确声明固定源/目标 Gemini 端点的 grounding 文本累计约定，不按模型名称推断。</summary>
public enum GeminiGroundingStreamMode
{
    /// <summary>不转换 grounding 流；保留既有严格诊断及显式显示损耗策略。</summary>
    Disabled,
    /// <summary>一个逻辑文本 part 累计增量，segment 使用累计文本的原生字节偏移，支持记录按到达顺序追加。</summary>
    SingleTextPart,
    /// <summary>仅 Responses→Gemini：按已验证的非交错文本块顺序拼接为一个累计 part，并精确平移引用。</summary>
    ConcatenatedTextParts
}
