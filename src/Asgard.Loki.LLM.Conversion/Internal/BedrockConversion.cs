using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>Nova/Converse 完整原生 JSON 的显式映射，不借用四协议中间文本或自动转换流。</summary>
internal static class BedrockConversion
{
    /// <summary>只识别两个已实现完整映射的独立原生协议。</summary>
    internal static bool IsBedrock(LlmProtocol protocol) => protocol is LlmProtocol.BedrockConverse or LlmProtocol.BedrockNova;

    /// <summary>按明确格式映射完整请求，始终保留独立原文和选项上下文。</summary>
    internal static LlmConversionResult Request(JsonObject source, LlmProtocol from, LlmProtocol to, LlmConversionOptions options)
    {
        if (options.BedrockNovaRequestFormat == BedrockNovaRequestFormat.Unspecified)
            throw new ArgumentException("Nova/Converse 请求转换必须显式选择 BedrockNovaRequestFormat。", nameof(options));
        if (options.TargetSettings is not null)
            throw new NotSupportedException("四协议 TargetSettings 尚不适用于 Bedrock 原生请求；不会忽略此验证声明。");
        var doc = new ConversionDocument();
        var nova = from == LlmProtocol.BedrockNova;
        foreach (var field in new[] { "outputConfig", "guardrailConfig", "promptVariables" })
            if (source.ContainsKey(field)) Reject("$." + field);
        Check(source, doc, "$", nova ? ["model", "schemaVersion", "system", "messages", "inferenceConfig", "toolConfig"]
            : ["model", "system", "messages", "inferenceConfig", "toolConfig", "additionalModelRequestFields"]);
        if (nova)
        {
            if (options.BedrockNovaRequestFormat == BedrockNovaRequestFormat.MessagesV1
                ? !source.ContainsKey("schemaVersion") || Text(source["schemaVersion"]) != "messages-v1"
                : source.ContainsKey("schemaVersion")) throw new ArgumentException("源 Nova 请求外壳与显式格式不匹配。");
        }
        var target = new JsonObject { ["model"] = Text(source["model"]) };
        if (!nova && options.BedrockNovaRequestFormat == BedrockNovaRequestFormat.MessagesV1) target["schemaVersion"] = "messages-v1";
        if (source.ContainsKey("system")) target["system"] = BedrockContentConversion.System(Array(source["system"]), doc);
        target["messages"] = BedrockContentConversion.Messages(Array(source["messages"]), doc);
        if (source.ContainsKey("toolConfig")) target["toolConfig"] = BedrockContentConversion.Tools(Object(source["toolConfig"]), doc);
        BedrockGenerationConversion.Map(source, target, nova, options, doc);
        LlmProtocolConverter.Enforce(doc, options);
        var history = Array(target["messages"]).SelectMany(message => Array(Object(message)["content"]))
            .Select(Object).Where(block => block.ContainsKey("toolUse")).Select(block => Text(Object(block["toolUse"])["toolUseId"]));
        return new(target, source, doc.Diagnostics, new(from, to, options, historicalIds: history));
    }

    /// <summary>只检查原生调用位置；业务参数里的同名字段不是工具状态。</summary>
    internal static void ValidateContext(JsonObject response, LlmConversionContext context)
    {
        foreach (var node in Array(Object(Object(response["output"])["message"])["content"]))
        {
            var block = Object(node);
            if (block["toolUse"] is JsonObject tool && context.HistoricalIds.Contains(Text(tool["toolUseId"])))
                Reject("$.output.message.content[].toolUse.toolUseId", "tool_call_id_reused");
        }
    }

    /// <summary>共有原生输出不归一化结束原因、计数或模型 ID，也不补造缺失用量。</summary>
    internal static LlmConversionResult Response(JsonObject source, LlmConversionOptions options)
    {
        var doc = new ConversionDocument();
        var target = Pick(source, doc, "$", "output", "stopReason", "usage", "metrics");
        var output = Object(source["output"]); Check(output, doc, "$.output", "message");
        var message = Object(output["message"]);
        if (Text(message["role"]) != "assistant") throw new ArgumentException("Bedrock 完整生成响应的角色必须是 assistant。");
        var calls = new ToolCallTracker();
        target["output"] = new JsonObject { ["message"] = BedrockContentConversion.Message(message, doc, calls, "$.output.message") };
        var stop = Text(source["stopReason"]);
        var hasCalls = Array(target["output"]!["message"]!["content"]).Any(item => Object(item).ContainsKey("toolUse"));
        if (stop.Length == 0 || stop == "tool_use" && !hasCalls || hasCalls && stop != "tool_use")
            throw new ArgumentException("Bedrock 原生结束原因与完整工具输出不一致。");
        var usage = Object(source["usage"]); var copiedUsage = Pick(usage, doc, "$.usage", "inputTokens", "outputTokens", "totalTokens");
        foreach (var key in new[] { "inputTokens", "outputTokens", "totalTokens" }) Nonnegative(usage[key]);
        target["usage"] = copiedUsage;
        var metrics = Object(source["metrics"]); Nonnegative(metrics["latencyMs"]);
        target["metrics"] = Pick(metrics, doc, "$.metrics", "latencyMs");
        LlmProtocolConverter.Enforce(doc, options); return new(target, source, doc.Diagnostics);
    }

    /// <summary>逐字段诊断后只复制已证明的共有字段，未知值留在完整原文。</summary>
    internal static JsonObject Pick(JsonObject source, ConversionDocument doc, string path, params string[] fields)
    {
        Check(source, doc, path, fields); JsonObject target = [];
        foreach (var field in fields) Copy(source, field, target, field);
        return target;
    }

    /// <summary>计数必须为实际非负 JSON 整数，不以缺省零替代缺失或 null。</summary>
    internal static void Nonnegative(JsonNode? node)
    {
        if (node is null || Number(node) < 0) throw new ArgumentException("Bedrock 原生计数必须是非负整数。");
    }

    /// <summary>原生状态和未知内容不借损耗许可伪装成另一种可执行协议。</summary>
    internal static void Reject(string path, string code = "bedrock_native_only")
        => throw new LlmConversionException([new(code, path, "此 Bedrock 原生内容不能由当前显式映射安全表示。")]);
}
