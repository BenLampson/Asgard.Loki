using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;
using static Asgard.Loki.LLM.Conversion.Internal.ToolRequestCodec;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>按 call_id 消费历史结果，并校验可选的原始 namespace/name，禁止按显示名称配对。</summary>
internal static class ToolConversionHistory
{
    /// <summary>只改写副本，原始 item 元数据和完整源结构留在 OriginalData。</summary>
    internal static IReadOnlySet<string> Rewrite(JsonArray? history, IReadOnlyDictionary<ConvertedToolIdentity, ConvertedToolBinding> map,
        LlmProtocol target, LlmConversionOptions options)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal); var pending = new Dictionary<string, ConvertedToolIdentity>(StringComparer.Ordinal);
        if (history is null) return seen;
        foreach (var node in history)
        {
            var item = Object(node); var type = item["type"]?.GetValue<string>();
            if (type is null or "message") { Metadata(item); continue; }
            if (type is not ("function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output")) continue;
            var custom = type.StartsWith("custom_", StringComparison.Ordinal); var call = type.EndsWith("_call", StringComparison.Ordinal); var id = Name(item["call_id"]);
            Metadata(item);
            if (call)
            {
                Known(item, "$.input[]", custom ? ["type", "call_id", "name", "namespace", "input"] : ["type", "call_id", "name", "namespace", "arguments"]);
                var identity = ReadIdentity(item, custom, options);
                if (!seen.Add(id)) Reject("$.input[].call_id", "ambiguous_tool_call");
                if (seen.Count > options.MaxToolConversionHistoryCalls) Reject("$.input", "custom_tool_history_limit");
                pending.Add(id, identity);
                if (map.TryGetValue(identity, out var binding)) { item["name"] = binding.EncodedName; item.Remove("namespace"); }
                if (custom)
                {
                    item["arguments"] = CustomToolTextCodec.Encode(Text(item["input"]), options.MaxCustomToolInputBytes);
                    item["type"] = "function_call"; item.Remove("input");
                }
            }
            else
            {
                Known(item, "$.input[]", custom ? ["type", "call_id", "output"] : ["type", "call_id", "output", "name", "namespace"]);
                if (!pending.Remove(id, out var identity) || identity.IsCustom != custom) Reject("$.input[].call_id", "tool_result_kind_mismatch");
                if (item.ContainsKey("name") && Name(item["name"]) != identity!.Name
                    || item.ContainsKey("namespace") && Name(item["namespace"]) != identity!.Namespace) Reject("$.input[]", "tool_result_identity_mismatch");
                item.Remove("name"); item.Remove("namespace");
                if (custom)
                {
                    if (item["output"] is not JsonValue value || !value.TryGetValue<string>(out _)) Reject("$.input[].output", "custom_tool_result_content_not_supported");
                    var text = Text(item["output"]); CustomToolTextCodec.Validate(text, options.MaxCustomToolInputBytes);
                    if (target == LlmProtocol.Gemini) item["output"] = new JsonObject { ["output"] = text }.ToJsonString();
                    item["type"] = "function_call_output";
                }
            }
        }
        return seen;
    }

    /// <summary>源 item ID/actor 不是目标供应商 item ID；未完成项不能重放成完整历史。</summary>
    private static void Metadata(JsonObject item)
    {
        if (item.ContainsKey("id")) { _ = Name(item["id"]); item.Remove("id"); }
        if (item.ContainsKey("created_by")) { _ = Name(item["created_by"]); item.Remove("created_by"); }
        if (item.ContainsKey("status"))
        {
            if (Text(item["status"]) != "completed") Reject("$.input[].status", "unfinished_tool_history");
            item.Remove("status");
        }
    }
}
