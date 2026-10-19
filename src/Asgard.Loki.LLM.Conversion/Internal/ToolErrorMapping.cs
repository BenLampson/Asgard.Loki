using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>只在明确格式下保留 Claude/Gemini 错误语义；业务 JSON 不成为隐式错误标志。</summary>
internal static class ToolErrorMapping
{
    /// <summary>Claude 错误标记与原始内容格式独立保存，数组不能被串接或当成 JSON 业务数组。</summary>
    internal static JsonObject ReadClaude(JsonObject block, ConversionDocument doc)
    {
        var error = block.ContainsKey("is_error") && Boolean(block["is_error"]);
        if (error && doc.ToolErrorFormat == LlmToolErrorFormat.NativeOnly) Reject();
        if (error && block["content"] is JsonArray) Format();
        var result = ToolResultMapping.Read(Text(block["tool_use_id"]), block["content"], LlmProtocol.ClaudeMessages, doc);
        if (error) result["is_error"] = true;
        return result;
    }
    /// <summary>只识别 response 根级原生 error，业务对象内部的同名属性不触发错误判定。</summary>
    internal static JsonObject ReadGemini(string id, string name, JsonObject native, ConversionDocument doc)
    {
        var response = Object(native["response"]);
        var result = new JsonObject { ["type"] = "result", ["id"] = id, ["name"] = name };
        if (!response.ContainsKey("error")) { result["output"] = response.ToJsonString(); return result; }
        if (doc.ToolErrorFormat == LlmToolErrorFormat.NativeOnly) Reject("$parts[].functionResponse.response.error");
        if (native.ContainsKey("parts") || native.ContainsKey("willContinue") || native.ContainsKey("scheduling"))
            throw new LlmConversionException([new("tool_result_error_controls_not_representable", "$parts[].functionResponse", "同步错误格式不能删除媒体、持续回填或异步调度语义。")]);
        if (response["error"] is null) Format();
        if (doc.ToolErrorFormat == LlmToolErrorFormat.GeminiErrorText)
        {
            if (response.Count != 1 || response["error"] is not JsonValue value || !value.TryGetValue<string>(out _)) Format();
            var text = Text(response["error"]); ToolErrorContent.ValidateText(text, doc.MaxToolErrorBytes); result["output"] = text;
        }
        else result["output"] = ToolErrorContent.Json(response, doc.MaxToolErrorBytes);
        result["is_error"] = true; return result;
    }
    /// <summary>两个明确格式仅对 Claude/Gemini 有等价语义，普通损耗许可不能启用其他目标。</summary>
    internal static void ValidateTarget(JsonObject result, LlmProtocol protocol, ConversionDocument doc)
    {
        if (!IsError(result)) return;
        if (doc.ToolErrorFormat == LlmToolErrorFormat.NativeOnly || protocol is not (LlmProtocol.ClaudeMessages or LlmProtocol.Gemini)) Reject();
        if (result.ContainsKey("parts")) Format();
        ToolErrorContent.ValidateText(Text(result["output"]), doc.MaxToolErrorBytes);
    }
    /// <summary>明确文本格式才封装 error；对象格式必须已携带原生错误，普通成功结果不能被误解释成错误。</summary>
    internal static JsonObject GeminiResponse(JsonObject result, JsonNode output, ConversionDocument doc)
    {
        if (IsError(result) && doc.ToolErrorFormat == LlmToolErrorFormat.GeminiErrorText) return new() { ["error"] = Text(output) };
        JsonObject? response;
        try { response = JsonNode.Parse(Text(output), documentOptions: new() { AllowDuplicateProperties = false }) as JsonObject; }
        catch (JsonException) when (IsError(result)) { Format(); throw; }
        if (response is null)
        {
            if (IsError(result)) Format();
            throw new LlmConversionException([new("tool_result_object_required", "$tool_result", "Gemini 工具结果必须是 JSON 对象，SDK 不隐式包装文本。")]);
        }
        if (IsError(result)) { if (!response.ContainsKey("error") || response["error"] is null) Format(); _ = ToolErrorContent.Json(response, doc.MaxToolErrorBytes); }
        else if (response.ContainsKey("error"))
            throw new LlmConversionException([new("tool_result_error_semantics_conflict", "$tool_result.response.error", "普通成功结果的 error 键不能隐式变成 Gemini 工具错误。")]);
        return response;
    }
    /// <summary>只读取转换器内部构造的错误标记，不递归检查用户数据。</summary>
    internal static bool IsError(JsonObject block) => block["is_error"]?.GetValue<bool>() == true;
    /// <summary>错误必须在目标保留原生语义，不因允许普通字段损耗而降级为成功。</summary>
    private static void Reject(string path = "$tool_result.is_error") => throw new LlmConversionException([new("tool_result_error_not_representable", path, "工具错误需要明确的 Claude/Gemini 格式和等价目标。")]);
    /// <summary>格式不一致不能猜测、重包装或省略 JSON 成员。</summary>
    private static void Format() => throw new LlmConversionException([new("tool_result_error_format_not_representable", "$tool_result.content", "工具错误内容与声明的原生格式不一致。")]);
}
