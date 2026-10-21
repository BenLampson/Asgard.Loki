namespace Asgard.Loki.LLM.Conversion;

/// <summary>显式声明 Claude/Gemini 工具错误的内容格式，不推测字符串是否为业务 JSON。</summary>
public enum LlmToolErrorFormat
{
    /// <summary>默认只允许原生协议保留工具错误，跨协议不能删除错误语义。</summary>
    NativeOnly = 0,
    /// <summary>Claude 错误字符串是完整 Gemini response JSON 对象，必须包含非 null error；原样保留所有成员。</summary>
    GeminiResponseObject = 1,
    /// <summary>Claude 错误是原始文本，与 Gemini 仅有一个字符串 error 成员的 response 对象互转。</summary>
    GeminiErrorText = 2
}
