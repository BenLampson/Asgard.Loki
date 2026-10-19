using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>转换普通函数工具及工具选择，托管工具不伪装成普通函数。</summary>
internal static class ToolMapping
{
    /// <summary>规范化函数定义与工具选择。</summary>
    internal static void Read(JsonObject source, LlmProtocol protocol, ConversionDocument doc)
    {
        if (source["tools"] is JsonArray tools)
        {
            foreach (var node in tools)
            {
                var tool = Object(node);
                if (protocol == LlmProtocol.Gemini)
                {
                    Check(tool, doc, "$.tools[]", "functionDeclarations");
                    if (tool.ContainsKey("functionDeclarations"))
                        foreach (var function in Array(tool["functionDeclarations"])) Add(Object(function), protocol, doc);
                }
                else
                {
                    if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses)
                    {
                        if (tool["type"]?.GetValue<string>() != "function") { Loss(doc, "$.tools[].type", "unsupported_hosted_tool"); continue; }
                        if (protocol == LlmProtocol.ChatCompletions) Check(tool, doc, "$.tools[]", "type", "function");
                    }
                    Add(protocol == LlmProtocol.ChatCompletions ? Object(tool["function"]) : tool, protocol, doc);
                }
            }
        }
        else if (source.ContainsKey("tools")) throw new ArgumentException("tools 必须是数组。");
        if (protocol == LlmProtocol.Gemini)
        {
            if (!source.ContainsKey("toolConfig")) return;
            var config = Object(source["toolConfig"]);
            Check(config, doc, "$.toolConfig", "functionCallingConfig");
            var choice = Object(config["functionCallingConfig"]);
            Check(choice, doc, "$.toolConfig.functionCallingConfig", "mode", "allowedFunctionNames");
            var mode = Text(choice["mode"]);
            if (choice.ContainsKey("allowedFunctionNames"))
            {
                var names = Array(choice["allowedFunctionNames"]);
                if (mode != "ANY" || names.Count != 1) { Loss(doc, "$.toolConfig.functionCallingConfig.allowedFunctionNames"); return; }
                doc.ToolChoice = new JsonObject { ["name"] = Text(names[0]) };
            }
            else doc.ToolChoice = mode switch { "AUTO" => JsonValue.Create("auto"), "ANY" => JsonValue.Create("required"), "NONE" => JsonValue.Create("none"), _ => throw new NotSupportedException("不支持此 Gemini 工具选择模式。") };
            return;
        }
        if (!source.TryGetPropertyValue("tool_choice", out var selected)) return;
        if (selected is JsonValue) { doc.ToolChoice = selected.DeepClone(); return; }
        var selectedObject = Object(selected);
        var type = Text(selectedObject["type"]);
        Check(selectedObject, doc, "$.tool_choice", protocol == LlmProtocol.ClaudeMessages
            ? type == "tool" ? ["type", "name", "disable_parallel_tool_use"] : ["type", "disable_parallel_tool_use"]
            : type is "auto" or "none" or "any" ? ["type"] : protocol == LlmProtocol.ChatCompletions ? ["type", "function"] : ["type", "name"]);
        if (protocol == LlmProtocol.ClaudeMessages && selectedObject.ContainsKey("disable_parallel_tool_use"))
        {
            if (type == "none") throw new ArgumentException("Claude none 工具选择不接受并行控制。");
            doc.Settings["parallel"] = !Boolean(selectedObject["disable_parallel_tool_use"]);
        }
        if (type is "auto" or "none" or "any") doc.ToolChoice = JsonValue.Create(type == "any" ? "required" : type);
        else if (type is "function" or "tool")
        {
            var function = selectedObject["function"] as JsonObject;
            if (function is not null) Check(function, doc, "$.tool_choice.function", "name");
            doc.ToolChoice = new JsonObject { ["name"] = Text(function?["name"] ?? selectedObject["name"]) };
        }
        else Loss(doc, "$.tool_choice", "unsupported_tool_choice");
    }

    /// <summary>保留 JSON Schema 原文；Gemini typed 参数显式转换，目标仍使用 parametersJsonSchema。</summary>
    private static void Add(JsonObject tool, LlmProtocol protocol, ConversionDocument doc)
    {
        var schemaKey = protocol == LlmProtocol.ClaudeMessages ? "input_schema" : protocol == LlmProtocol.Gemini ? "parametersJsonSchema" : "parameters";
        Check(tool, doc, "$.tools[].function", protocol == LlmProtocol.Responses || protocol == LlmProtocol.ClaudeMessages
            ? ["type", "name", "description", schemaKey, "strict"] : protocol == LlmProtocol.Gemini
                ? ["name", "description", schemaKey, "parameters", "strict"] : ["name", "description", schemaKey, "strict"]);
        if (protocol == LlmProtocol.ClaudeMessages && tool.ContainsKey("type") && tool["type"]?.GetValue<string>() != "custom")
        { Loss(doc, "$.tools[].type", "unsupported_hosted_tool"); return; }
        var function = new JsonObject { ["name"] = Text(tool["name"]) };
        Copy(tool, "description", function, "description");
        Copy(tool, schemaKey, function, "parameters");
        if (protocol == LlmProtocol.Gemini && tool.ContainsKey("parameters"))
        {
            if (tool.ContainsKey("parametersJsonSchema")) throw new ArgumentException("Gemini 函数不能同时指定两种参数 Schema 方言。");
            // 普通工具参数不保证目标生成属性顺序；不能套用文本结构化输出的顺序承诺。
            function["parameters"] = GeminiSchemaMapping.Read(Object(tool["parameters"]), doc, "$.tools[].function.parameters", LlmProtocol.None);
        }
        Copy(tool, "strict", function, "strict");
        if (!function.ContainsKey("parameters")) function["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        doc.Tools.Add(function);
    }

    /// <summary>将规范函数与选择写入目标协议。</summary>
    internal static void Write(JsonObject target, LlmProtocol protocol, ConversionDocument doc)
    {
        var tools = new JsonArray();
        foreach (var node in doc.Tools)
        {
            var function = Object(node).DeepClone().AsObject();
            if (protocol == LlmProtocol.ChatCompletions) tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            else
            {
                if (protocol == LlmProtocol.Responses) function["type"] = "function";
                if (protocol == LlmProtocol.ClaudeMessages) { function["input_schema"] = function["parameters"]?.DeepClone(); function.Remove("parameters"); }
                if (protocol == LlmProtocol.Gemini)
                {
                    if (function.Remove("strict")) Loss(doc, "$.tools[].strict");
                    function["parametersJsonSchema"] = function["parameters"]?.DeepClone(); function.Remove("parameters");
                }
                tools.Add(function);
            }
        }
        if (tools.Count != 0) target["tools"] = protocol == LlmProtocol.Gemini ? new JsonArray(new JsonObject { ["functionDeclarations"] = tools }) : tools;
        if (doc.ToolChoice is null) return;
        var name = doc.ToolChoice is JsonObject named ? Text(named["name"]) : null;
        var mode = name is null ? Text(doc.ToolChoice) : "named";
        if (mode is not ("auto" or "none" or "required" or "named")) throw new ArgumentException("工具选择必须为 auto、none、required 或具名函数。");
        if (protocol == LlmProtocol.Gemini)
        {
            var config = new JsonObject { ["mode"] = mode switch { "auto" => "AUTO", "none" => "NONE", _ => "ANY" } };
            if (name is not null) config["allowedFunctionNames"] = new JsonArray(JsonValue.Create(name));
            target["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
        }
        else if (protocol == LlmProtocol.ClaudeMessages)
        {
            var choice = new JsonObject { ["type"] = mode switch { "required" => "any", "named" => "tool", _ => mode } };
            if (name is not null) choice["name"] = name;
            target["tool_choice"] = choice;
        }
        else if (name is null) target["tool_choice"] = mode;
        else target["tool_choice"] = protocol == LlmProtocol.Responses ? new JsonObject { ["type"] = "function", ["name"] = name }
            : new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name } };
    }
}
