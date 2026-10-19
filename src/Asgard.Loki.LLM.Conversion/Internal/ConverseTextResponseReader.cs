using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>读取完整 Converse 的通用文本/普通工具输出；源原文另外保留，不补造指标或推理状态。</summary>
internal static class ConverseTextResponseReader
{
    /// <summary>目标需要的 ID 是 SDK 合成标签，模型标签来自请求上下文而非原生返回的 model。</summary>
    internal static ConversionDocument Read(JsonObject source, LlmConversionContext? context)
    {
        var doc = new ConversionDocument { Id = "loki_" + Guid.NewGuid().ToString("N"), Model = context?.RequestedModel ?? "" };
        if (source["error"] is not null) throw new LlmConversionException([new("upstream_error", "$.error", "错误响应不能转换为成功响应。")]);
        Check(source, doc, "$", "output", "stopReason", "usage", "metrics");
        var output = Object(source["output"]); Check(output, doc, "$.output", "message");
        var native = Object(output["message"]); Check(native, doc, "$.output.message", "role", "content");
        if (Text(native["role"]) != "assistant") throw new ArgumentException("Converse 生成响应必须是 assistant。");
        var message = new ConversionMessage("assistant"); var calls = new ToolCallTracker();
        foreach (var node in Array(native["content"]))
        {
            var block = Object(node);
            if (new[] { "text", "toolUse" }.Count(block.ContainsKey) != 1 || NativeOnly(block))
                BedrockConversion.Reject("$.output.message.content[]", "converse_content_not_representable");
            if (block.ContainsKey("text"))
            {
                Check(block, doc, "$.output.message.content[]", "text"); message.Blocks.Add(new() { ["type"] = "text", ["text"] = Text(block["text"]) });
            }
            else
            {
                Check(block, doc, "$.output.message.content[]", "toolUse"); var tool = Object(block["toolUse"]);
                if (tool.ContainsKey("type")) BedrockConversion.Reject("$.output.message.content[].toolUse.type");
                Check(tool, doc, "$.output.message.content[].toolUse", "toolUseId", "name", "input");
                var id = Text(tool["toolUseId"]); var name = Text(tool["name"]); calls.Register(id, name);
                if (context?.HistoricalIds.Contains(id) == true) BedrockConversion.Reject("$.output.message.content[].toolUse.toolUseId", "tool_call_id_reused");
                var arguments = Object(tool["input"]).ToJsonString(); _ = ConverseTextContent.ParseObject(arguments);
                message.Blocks.Add(new() { ["type"] = "call", ["id"] = id, ["name"] = name, ["arguments"] = arguments });
            }
        }
        doc.Messages.Add(message);
        doc.Finish = Text(source["stopReason"]) switch { "end_turn" or "stop_sequence" => "stop", "tool_use" => "tool_calls", "max_tokens" => "length", "content_filtered" => "content_filter",
            _ => throw new LlmConversionException([new("unsupported_finish_reason", "$.stopReason", "此原生终止原因尚无明确等价文本协议表示。")]) };
        ResponseMapping.ValidateFinish(doc);
        var usage = Object(source["usage"]); Check(usage, doc, "$.usage", "inputTokens", "outputTokens", "totalTokens", "cacheReadInputTokens", "cacheWriteInputTokens", "cacheDetails");
        doc.Usage["input"] = Counter(usage["inputTokens"]); doc.Usage["output"] = Counter(usage["outputTokens"]); doc.Usage["total"] = Counter(usage["totalTokens"]);
        foreach (var name in new[] { "cacheReadInputTokens", "cacheWriteInputTokens" })
            if (usage.ContainsKey(name))
            {
                if (Counter(usage[name]) != 0) BedrockConversion.Reject("$.usage." + name, "converse_cache_accounting_not_verified");
                doc.Usage[name == "cacheReadInputTokens" ? "cached" : "createdCache"] = 0;
            }
        if (usage.ContainsKey("cacheDetails") && Array(usage["cacheDetails"]).Count != 0) BedrockConversion.Reject("$.usage.cacheDetails", "converse_cache_accounting_not_verified");
        var metrics = Object(source["metrics"]); _ = Counter(metrics["latencyMs"]); Check(metrics, doc, "$.metrics", "latencyMs");
        return doc;
    }
    /// <summary>已知不透明/执行/媒体块不因普通损耗许可被丢弃，业务 JSON 内部不被遍历猜测。</summary>
    private static bool NativeOnly(JsonObject block) => block.Any(field => field.Key is "reasoningContent" or "toolResult" or "image" or "video" or "document" or "citationsContent" or "guardContent" or "cachePoint" or "audio" or "searchResult");
    /// <summary>所有原生必需计数显式存在，不由其他计数求和或补零。</summary>
    private static long Counter(JsonNode? node)
    {
        var value = JsonSerializer.SerializeToElement(node);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0) throw new InvalidDataException("Converse 必需计数/指标必须为非负原生整数。"); return number;
    }
}
