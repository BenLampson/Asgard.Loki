using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>文本公开请求到原生 Converse 以及原生完整响应的反向投影，独立于 Nova 原生协议对。</summary>
internal static class ConverseTextConversion
{
    /// <summary>仅识别已有规范化读取器的四种文本协议。</summary>
    internal static bool IsText(LlmProtocol protocol) => protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses or LlmProtocol.ClaudeMessages or LlmProtocol.Gemini;
    /// <summary>复用已验证私有中间表示，不借用供应商包、HTTP 或其他协议请求作绕行。</summary>
    internal static LlmConversionResult Request(JsonObject source, LlmProtocol from, LlmConversionOptions options)
    {
        if (!IsText(from)) throw new NotSupportedException("此入口只接受文本协议到 Converse 的请求。");
        if (options.TargetSettings is not null) throw new NotSupportedException("现有四协议 TargetSettings 尚不适用于 Converse 目标。");
        if (source.ContainsKey("stream") && Boolean(source["stream"]))
            throw new NotSupportedException("文本协议到 Converse 的流尚未实现，不把 stream 请求降级为完整调用。");
        GuardTools(source, from); ConverseTextSourceGuard.Validate(source, from);
        var doc = RequestReader.Read(source, from, LlmProtocol.BedrockConverse, options);
        doc.Diagnostics.AddRange(HostedSearchOutput.Inspect(source, from));
        var target = ConverseTextRequestWriter.Write(doc, from, options);
        LlmProtocolConverter.Enforce(doc, options);
        var history = doc.Messages.SelectMany(message => message.Blocks).Where(block => Text(block["type"]) == "call").Select(block => Text(block["id"]));
        return new(target, source, doc.Diagnostics, new(from, LlmProtocol.BedrockConverse, options, historicalIds: history, requestedModel: doc.Model));
    }
    /// <summary>已有目标写出器负责正确原生结果形状，源模型缺省不冒称为真实返回值。</summary>
    internal static LlmConversionResult Response(JsonObject source, LlmProtocol to, LlmConversionOptions options, LlmConversionContext? context = null)
    {
        var doc = ConverseTextResponseReader.Read(source, context); var target = ResponseMapping.Write(doc, to);
        LlmProtocolConverter.Enforce(doc, options); return new(target, source, doc.Diagnostics, context);
    }
    /// <summary>托管/custom/namespace 或异步工具控制不能因普通字段损耗许可变成普通函数。</summary>
    private static void GuardTools(JsonObject source, LlmProtocol protocol)
    {
        foreach (var node in source["tools"] as JsonArray ?? [])
        {
            var tool = Object(node);
            if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses && Text(tool["type"]) != "function"
                || protocol == LlmProtocol.ClaudeMessages && tool.ContainsKey("type") && Text(tool["type"]) != "custom"
                || protocol == LlmProtocol.Gemini && (tool.Count != 1 || !tool.ContainsKey("functionDeclarations")))
                BedrockConversion.Reject("$.tools[]", "converse_tool_not_representable");
            if (protocol == LlmProtocol.Gemini)
                foreach (var function in Array(tool["functionDeclarations"]))
                    if (Object(function).ContainsKey("behavior")) BedrockConversion.Reject("$.tools[].functionDeclarations[].behavior");
        }
        if (protocol != LlmProtocol.Gemini || source["contents"] is not JsonArray contents) return;
        foreach (var message in contents)
        foreach (var part in Array(Object(message)["parts"]))
            if (Object(part)["functionResponse"] is JsonObject response && new[] { "parts", "willContinue", "scheduling" }.Any(response.ContainsKey))
                BedrockConversion.Reject("$.contents[].parts[].functionResponse", "converse_tool_controls_not_representable");
    }
}
