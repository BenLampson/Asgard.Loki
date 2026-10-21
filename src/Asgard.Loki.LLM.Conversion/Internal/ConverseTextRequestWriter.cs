using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>写出明确的 Converse 通用请求，不按模型名推断 additionalModelRequestFields 或思考方言。</summary>
internal static class ConverseTextRequestWriter
{
    /// <summary>所有正文控制保持明确；工具 Schema 与输出 Schema 分别写入原生位置。</summary>
    internal static JsonObject Write(ConversionDocument doc, LlmProtocol source, LlmConversionOptions options)
    {
        var target = new JsonObject { ["model"] = doc.Model };
        if (doc.Settings.ContainsKey("parallel")) BedrockConversion.Reject("$settings.parallel", "converse_parallel_control_not_representable");
        JsonArray system = []; var seenMessage = false;
        foreach (var message in doc.Messages)
        {
            if (message.Role is not ("system" or "developer")) { seenMessage = true; continue; }
            if (seenMessage) Loss(doc, "$messages[].role", "instruction_order_changed");
            if (message.Role == "developer") Loss(doc, "$messages[].role", "role_semantics_changed");
            foreach (var block in message.Blocks)
            {
                if (Text(block["type"]) != "text") BedrockConversion.Reject("$system");
                system.Add(new JsonObject { ["text"] = Text(block["text"]) });
            }
        }
        if (system.Count != 0) target["system"] = system;
        target["messages"] = ConverseTextContent.WriteMessages(doc, source);
        JsonObject inference = [];
        foreach (var field in doc.Settings)
        {
            if (field.Key == "topK") continue;
            var key = field.Key switch { "maxTokens" => "maxTokens", "temperature" => "temperature", "topP" => "topP", "stop" => "stopSequences", _ => null };
            if (key is null) BedrockConversion.Reject("$settings." + field.Key);
            if (key == "maxTokens") { BedrockConversion.Nonnegative(field.Value); if (Number(field.Value) == 0) throw new ArgumentException("maxTokens 必须为正整数。"); }
            else if (key == "stopSequences")
            {
                var stops = field.Value is JsonValue ? new JsonArray(Text(field.Value)) : Array(field.Value).DeepClone().AsArray();
                if (stops.Count > 2500 || stops.Any(stop => Text(stop).Length == 0)) throw new ArgumentException("Converse 停止串必须非空，最多 2500 项。");
                inference[key] = stops; continue;
            }
            else
            {
                var value = JsonSerializer.SerializeToElement(field.Value);
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1)
                    throw new ArgumentException("Converse 通用采样值必须为 0 至 1 的有限原生数值，不自动缩放或裁剪。");
            }
            inference[key!] = field.Value?.DeepClone();
        }
        if (inference.Count != 0) target["inferenceConfig"] = inference;
        WriteTools(target, doc); WriteOutput(target, doc);
        ConverseTextReasoning.Write(target, doc, source, options); return target;
    }
    /// <summary>普通 JSON Schema 不删约束；具名选择保留原名，none 不以删除工具来伪装支持。</summary>
    private static void WriteTools(JsonObject target, ConversionDocument doc)
    {
        JsonArray tools = []; HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var node in doc.Tools)
        {
            var tool = Object(node); var name = Text(tool["name"]);
            if (name.Length is < 1 or > 64 || name.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('_' or '-')) || !names.Add(name))
                throw new ArgumentException("Converse 工具名称无效或重复，SDK 不重命名工具。");
            var spec = new JsonObject { ["name"] = name, ["inputSchema"] = new JsonObject { ["json"] = Object(tool["parameters"]).DeepClone() } };
            if (tool.ContainsKey("description") && Text(tool["description"]).Length == 0) throw new ArgumentException("Converse 工具描述若提供则不能为空。");
            if (tool.ContainsKey("strict")) _ = Boolean(tool["strict"]);
            Copy(tool, "description", spec, "description"); Copy(tool, "strict", spec, "strict"); tools.Add(new JsonObject { ["toolSpec"] = spec });
        }
        if (tools.Count == 0 && doc.ToolChoice is null) return;
        if (tools.Count == 0) throw new ArgumentException("Converse 工具选择必须有当前工具定义。");
        var config = new JsonObject { ["tools"] = tools };
        if (doc.ToolChoice is JsonValue value)
        {
            var mode = Text(value);
            if (mode == "none") BedrockConversion.Reject("$tool_choice", "converse_tool_choice_none_not_representable");
            if (mode is not ("auto" or "required")) throw new ArgumentException("未知工具选择模式。");
            config["toolChoice"] = new JsonObject { [mode == "required" ? "any" : "auto"] = new JsonObject() };
        }
        else if (doc.ToolChoice is JsonObject named)
        {
            var name = Text(named["name"]); if (!names.Contains(name)) throw new ArgumentException("具名工具选择必须匹配原始工具定义。");
            config["toolChoice"] = new JsonObject { ["tool"] = new JsonObject { ["name"] = name } };
        }
        target["toolConfig"] = config;
    }
    /// <summary>只改变结构化输出外壳，原生 schema 是 JSON 文本，name/description 省略时不补造。</summary>
    private static void WriteOutput(JsonObject target, ConversionDocument doc)
    {
        if (doc.OutputFormat is not { } format) return;
        if (Text(format["type"]) != "json_schema") BedrockConversion.Reject("$format", "converse_output_format_not_representable");
        if (format["strict"]?.GetValue<bool>() != true) Loss(doc, "$format.strict", "schema_enforcement_changed");
        var definition = new JsonObject { ["schema"] = Object(format["schema"]).ToJsonString() };
        Copy(format, "name", definition, "name"); Copy(format, "description", definition, "description");
        target["outputConfig"] = new JsonObject { ["textFormat"] = new JsonObject { ["type"] = "json_schema", ["structure"] = new JsonObject { ["jsonSchema"] = definition } } };
    }
}
