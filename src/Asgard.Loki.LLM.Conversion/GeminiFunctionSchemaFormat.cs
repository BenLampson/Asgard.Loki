namespace Asgard.Loki.LLM.Conversion;

/// <summary>独立选择 Gemini 目标函数参数方言，不与文本输出外壳互相覆盖。</summary>
public enum GeminiFunctionSchemaFormat
{
    /// <summary>默认原样保留 JSON Schema，写入 parametersJsonSchema。</summary>
    JsonSchema = 0,
    /// <summary>显式转换为 typed parameters；不删除不支持的校验约束。</summary>
    LegacySchema = 1
}
