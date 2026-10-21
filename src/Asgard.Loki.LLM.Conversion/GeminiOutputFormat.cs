namespace Asgard.Loki.LLM.Conversion;

/// <summary>显式选择 Gemini 目标的结构化文本外壳，不根据模型名或账号猜测。</summary>
public enum GeminiOutputFormat
{
    /// <summary>保留现有 responseMimeType/responseJsonSchema 输出。</summary>
    LegacyJsonSchema = 0,
    /// <summary>使用现行 responseFormat.text 及其 MIME 枚举。</summary>
    ResponseFormat = 1,
    /// <summary>显式转换为旧 responseMimeType/responseSchema；不删除不支持的校验约束。</summary>
    LegacySchema = 2
}
