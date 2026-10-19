using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>显式把 Responses 自由文本 custom 工具编码为普通函数；状态只属于一次请求。</summary>
internal static class CustomToolRequestCodec
{
    /// <summary>先收集所有名称再生成带随机隔离标识的编码名，防止跨请求还原和命名冲突。</summary>
    internal static LlmConversionContext Prepare(JsonObject request, LlmProtocol source, LlmProtocol target,
        LlmConversionOptions options, out JsonObject prepared)
    {
        prepared = request;
        if (!options.EnableCustomToolConversion || source != LlmProtocol.Responses || source == target)
            return new(source, target, options);
        var definitions = request["tools"] as JsonArray;
        var history = request["input"] as JsonArray;
        var active = definitions?.Any(node => Object(node)["type"]?.GetValue<string>() == "custom") == true
            || history?.Any(node => Object(node)["type"]?.GetValue<string>() is "custom_tool_call" or "custom_tool_call_output") == true;
        if (!active) return new(source, target, options);
        var customNames = new List<string>(); var declared = new HashSet<string>(StringComparer.Ordinal);
        var ordinary = new HashSet<string>(StringComparer.Ordinal); var reserved = new HashSet<string>(StringComparer.Ordinal);
        if (definitions is not null)
            foreach (var node in definitions)
            {
                var tool = Object(node); var type = Text(tool["type"]);
                if (tool["name"] is not null) reserved.Add(Text(tool["name"]));
                if (type == "custom")
                {
                    var name = Name(tool["name"]);
                    if (!declared.Add(name)) Reject("$.tools[].name", "ambiguous_custom_tool");
                    ValidateDefinition(tool); customNames.Add(name);
                }
                else if (type == "function")
                {
                    Known(tool, "$.tools[]", "type", "name", "description", "parameters", "strict");
                    if (!ordinary.Add(Name(tool["name"]))) Reject("$.tools[].name", "ambiguous_tool_definition");
                }
                LimitNames(customNames.Count + ordinary.Count, options);
            }
        if (history is not null)
            foreach (var node in history)
            {
                var item = Object(node); var type = item["type"]?.GetValue<string>();
                if (type is not ("function_call" or "custom_tool_call")) continue;
                var name = Name(item["name"]); reserved.Add(name);
                if (type == "function_call") ordinary.Add(name);
                else if (!customNames.Contains(name, StringComparer.Ordinal)) customNames.Add(name);
                LimitNames(customNames.Count + ordinary.Count, options);
            }
        if (customNames.Count == 0) Reject("$.input[].call_id", "tool_result_kind_mismatch");
        if (definitions is not null && definitions.Any(node => Object(node)["type"]?.GetValue<string>() is not ("custom" or "function")))
            Reject("$.tools", "custom_tool_combination_not_supported");
        var nonce = Guid.NewGuid().ToString("N"); var map = new Dictionary<string, CustomToolBinding>(StringComparer.Ordinal);
        foreach (var name in customNames)
        {
            var index = map.Count; string encoded;
            do { encoded = "loki_custom_" + nonce + "_" + index++; } while (!reserved.Add(encoded));
            map.Add(name, new(name, encoded));
        }
        prepared = request.DeepClone().AsObject();
        if (prepared["tools"] is JsonArray tools)
            for (var index = 0; index < tools.Count; index++)
            {
                var tool = Object(tools[index]); if (Text(tool["type"]) != "custom") continue;
                var function = new JsonObject { ["type"] = "function", ["name"] = map[Text(tool["name"])].EncodedName,
                    ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}""") };
                Copy(tool, "description", function, "description"); tools[index] = function;
            }
        var historicalIds = RewriteHistory(prepared["input"] as JsonArray, map, target, options);
        if (prepared["tool_choice"] is JsonObject choice && choice["type"]?.GetValue<string>() == "custom")
        {
            Known(choice, "$.tool_choice", "type", "name"); var name = Name(choice["name"]);
            if (!declared.Contains(name)) Reject("$.tool_choice.name", "unknown_custom_tool");
            choice["type"] = "function"; choice["name"] = map[name].EncodedName;
        }
        else if (prepared["tool_choice"] is JsonObject otherChoice)
        {
            var type = Text(otherChoice["type"]);
            if (type == "function")
            {
                Known(otherChoice, "$.tool_choice", "type", "name");
                if (!ordinary.Contains(Name(otherChoice["name"]))) Reject("$.tool_choice.name", "tool_choice_kind_mismatch");
            }
            else if (type is "auto" or "none" or "any") Known(otherChoice, "$.tool_choice", "type");
            else Reject("$.tool_choice", "custom_tool_choice_not_supported");
        }
        return new(source, target, options, map.Values, ordinary, historicalIds);
    }

    /// <summary>关联所有历史调用与结果，custom/普通结果类型不能互换；Gemini 的显式封装只用于 custom。</summary>
    private static IReadOnlySet<string> RewriteHistory(JsonArray? history, IReadOnlyDictionary<string, CustomToolBinding> map, LlmProtocol target, LlmConversionOptions options)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (history is null) return seen;
        foreach (var node in history)
        {
            var item = Object(node); var type = item["type"]?.GetValue<string>();
            if (type is not ("function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output")) continue;
            var custom = type.StartsWith("custom_", StringComparison.Ordinal); var call = type.EndsWith("_call", StringComparison.Ordinal);
            var id = Name(item["call_id"]);
            if (call)
            {
                if (!seen.Add(id)) Reject("$.input[].call_id", "ambiguous_tool_call");
                if (seen.Count > options.MaxToolConversionHistoryCalls) Reject("$.input", "custom_tool_history_limit");
                pending.Add(id, custom);
            }
            else if (!pending.Remove(id, out var expected) || custom != expected) Reject("$.input[].call_id", "tool_result_kind_mismatch");
            HistoryMetadata(item);
            if (!custom)
            {
                Known(item, "$.input[]", call ? ["type", "call_id", "name", "arguments"] : ["type", "call_id", "output"]); continue;
            }
            if (call)
            {
                Known(item, "$.input[]", "type", "call_id", "name", "input");
                item["arguments"] = CustomToolTextCodec.Encode(Text(item["input"]), options.MaxCustomToolInputBytes);
                item["name"] = map[Text(item["name"])].EncodedName; item["type"] = "function_call"; item.Remove("input");
            }
            else
            {
                Known(item, "$.input[]", "type", "call_id", "output");
                if (item["output"] is not JsonValue value || !value.TryGetValue<string>(out var output))
                    Reject("$.input[].output", "custom_tool_result_content_not_supported");
                var text = Text(item["output"]); CustomToolTextCodec.Validate(text, options.MaxCustomToolInputBytes);
                if (target == LlmProtocol.Gemini) item["output"] = new JsonObject { ["output"] = text }.ToJsonString();
                item["type"] = "function_call_output";
            }
        }
        return seen;
    }

    /// <summary>已完成的源 item 身份/状态只保留于 OriginalData，不冒充另一供应商的 item ID。</summary>
    private static void HistoryMetadata(JsonObject item)
    {
        if (item.ContainsKey("id")) { _ = Name(item["id"]); item.Remove("id"); }
        if (item.ContainsKey("created_by")) { _ = Name(item["created_by"]); item.Remove("created_by"); }
        if (item.ContainsKey("status"))
        {
            if (Text(item["status"]) != "completed") Reject("$.input[].status", "unfinished_tool_history");
            item.Remove("status");
        }
    }
    /// <summary>CFG/程序调用/延迟与异步语义不能被换成普通函数后假称等价。</summary>
    private static void ValidateDefinition(JsonObject tool)
    {
        Known(tool, "$.tools[]", "type", "name", "description", "format");
        if (tool.ContainsKey("description")) _ = Text(tool["description"]);
        if (!tool.ContainsKey("format")) return;
        var format = Object(tool["format"]);
        if (Text(format["type"]) != "text") Reject("$.tools[].format", "custom_tool_format_not_supported");
        Known(format, "$.tools[].format", "type");
    }
    /// <summary>未知字段在显式 custom 编码中也始终硬失败，不受普通损耗开关影响。</summary>
    private static void Known(JsonObject value, string path, params string[] fields)
    {
        foreach (var key in value.Select(pair => pair.Key)) if (!fields.Contains(key, StringComparer.Ordinal)) Reject(path + "." + key, "unsupported_custom_tool_field");
    }
    /// <summary>还原所需的普通名称表也必须有界，而非只限制 custom 定义数。</summary>
    private static void LimitNames(int count, LlmConversionOptions options)
    {
        if (count > options.MaxToolConversionMappings) Reject("$.tools", "custom_tool_mapping_limit");
    }
    /// <summary>空白关联标识和名称不能进入映射。</summary>
    private static string Name(JsonNode? node) { var name = Text(node); ArgumentException.ThrowIfNullOrWhiteSpace(name); return name; }
    /// <summary>映射安全约束不能由 AllowLoss 绕过，消息不回显用户文本。</summary>
    internal static void Reject(string path, string code) => throw new LlmConversionException([new(code, path, "custom 工具无法在当前请求上下文中保真转换。")]);
}
