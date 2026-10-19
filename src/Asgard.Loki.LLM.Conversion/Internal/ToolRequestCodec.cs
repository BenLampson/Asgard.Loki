using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>显式展开 namespace/custom 工具，所有身份映射仅属于本次请求。</summary>
internal static class ToolRequestCodec
{
    /// <summary>收集原始三元身份后生成隔离名称，不用分隔符拼接源名称。</summary>
    internal static LlmConversionContext Prepare(JsonObject request, LlmProtocol source, LlmProtocol target,
        LlmConversionOptions options, out JsonObject prepared, out IReadOnlyList<LlmConversionDiagnostic> diagnostics)
    {
        prepared = request; diagnostics = [];
        if (source != LlmProtocol.Responses || source == target) return new(source, target, options);
        var definitions = request["tools"] as JsonArray; var history = request["input"] as JsonArray;
        var namespaced = definitions?.Any(n => Object(n)["type"]?.GetValue<string>() == "namespace") == true
            || history?.Any(n => Object(n)["type"]?.GetValue<string>() is "function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output"
                && Object(n).ContainsKey("namespace")) == true
            || request["tool_choice"] is JsonObject choice && choice.ContainsKey("namespace");
        if (namespaced && !options.EnableNamespacedToolConversion) Reject("$.tools", "namespace_conversion_not_enabled");
        var custom = definitions?.Any(n => Object(n)["type"]?.GetValue<string>() == "custom") == true
            || history?.Any(n => Object(n)["type"]?.GetValue<string>() is "custom_tool_call" or "custom_tool_call_output") == true;
        if (!namespaced && !(custom && options.EnableCustomToolConversion)) return new(source, target, options);

        var identities = new List<ConvertedToolIdentity>(); var unique = new HashSet<ConvertedToolIdentity>();
        var declared = new HashSet<ConvertedToolIdentity>(); var reserved = new HashSet<string>(StringComparer.Ordinal);
        var flattened = new List<(ConvertedToolIdentity Identity, JsonObject Definition)>();
        var losses = new List<LlmConversionDiagnostic>();
        if (definitions is not null) ReadDefinitions(definitions, flattened, declared, losses, options);
        foreach (var (identity, _) in flattened) Register(identity);
        if (history is not null)
            foreach (var node in history)
            {
                var item = Object(node); var type = item["type"]?.GetValue<string>();
                if (type is "function_call" or "custom_tool_call") Register(ReadIdentity(item, type == "custom_tool_call", options));
            }
        var nonce = Guid.NewGuid().ToString("N"); var map = new Dictionary<ConvertedToolIdentity, ConvertedToolBinding>();
        var ordinary = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            if (identity.Namespace is null && !identity.IsCustom) { ordinary.Add(identity.Name); continue; }
            var index = map.Count; string encoded;
            do { encoded = "loki_tool_" + nonce + "_" + index++; } while (!reserved.Add(encoded));
            map.Add(identity, new(identity, encoded));
        }
        if (map.Count == 0 && custom) Reject("$.input[].call_id", "tool_result_kind_mismatch");
        prepared = request.DeepClone().AsObject();
        if (definitions is not null)
        {
            var tools = new JsonArray();
            foreach (var (identity, original) in flattened)
            {
                var tool = original.DeepClone().AsObject();
                if (map.TryGetValue(identity, out var binding)) tool["name"] = binding.EncodedName;
                if (identity.IsCustom)
                {
                    tool.Remove("format"); tool["type"] = "function";
                    tool["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}""");
                }
                tools.Add(tool);
            }
            prepared["tools"] = tools;
        }
        var historicalIds = ToolConversionHistory.Rewrite(prepared["input"] as JsonArray, map, target, options);
        RewriteChoice(prepared, declared, map);
        diagnostics = System.Array.AsReadOnly(losses.ToArray());
        return new(source, target, options, map.Values, ordinary, historicalIds);

        // 根普通函数也占名称表容量；相同三元身份的历史记录只登记一次。
        void Register(ConvertedToolIdentity identity)
        {
            if (!unique.Add(identity)) return;
            identities.Add(identity); reserved.Add(identity.Name);
            if (unique.Count > options.MaxToolConversionMappings) Reject("$.tools", "custom_tool_mapping_limit");
        }
    }

    /// <summary>只展开规范允许的一层函数/custom 分组；未知字段和重复定义均硬失败。</summary>
    private static void ReadDefinitions(JsonArray definitions, List<(ConvertedToolIdentity Identity, JsonObject Definition)> flattened,
        HashSet<ConvertedToolIdentity> declared, List<LlmConversionDiagnostic> diagnostics, LlmConversionOptions options)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < definitions.Count; index++)
        {
            var tool = Object(definitions[index]); var type = Text(tool["type"]);
            if (type != "namespace") { Add(tool, null); continue; }
            Known(tool, "$.tools[]", "type", "name", "description", "tools"); var scope = Name(tool["name"]);
            if (!namespaces.Add(scope)) Reject("$.tools[].name", "ambiguous_tool_namespace");
            if (namespaces.Count > options.MaxToolConversionMappings) Reject("$.tools", "custom_tool_mapping_limit");
            if (Text(tool["description"]).Length != 0)
                diagnostics.Add(new("namespace_description_not_representable", "$.tools[" + index + "].description", "普通函数目标无法原样保留 namespace 组描述；原文保留在 OriginalData。"));
            foreach (var child in Array(tool["tools"])) Add(Object(child), scope);
        }

        // namespace 是单独身份分量，不写进函数描述或伪造工具执行参数。
        void Add(JsonObject tool, string? scope)
        {
            var type = Text(tool["type"]);
            if (type is not ("function" or "custom")) Reject("$.tools", "custom_tool_combination_not_supported");
            var identity = new ConvertedToolIdentity(scope, Name(tool["name"]), type == "custom"); ValidateEnabled(identity, options);
            if (identity.IsCustom) ValidateCustom(tool);
            else Known(tool, "$.tools[]", "type", "name", "description", "parameters", "strict");
            if (!declared.Add(identity)) Reject("$.tools[].name", identity.IsCustom ? "ambiguous_custom_tool" : "ambiguous_tool_definition");
            if (declared.Count > options.MaxToolConversionMappings) Reject("$.tools", "custom_tool_mapping_limit");
            flattened.Add((identity, tool));
        }
    }

    /// <summary>规范具名选择没有 namespace 选择器；不能把碰撞名称或点号当作隐式作用域。</summary>
    private static void RewriteChoice(JsonObject request, IReadOnlySet<ConvertedToolIdentity> declared,
        IReadOnlyDictionary<ConvertedToolIdentity, ConvertedToolBinding> map)
    {
        if (request["tool_choice"] is not JsonObject choice) return;
        var type = Text(choice["type"]);
        if (type is "function" or "custom")
        {
            Known(choice, "$.tool_choice", "type", "name"); var name = Name(choice["name"]); var custom = type == "custom";
            if (declared.Any(t => t.Name == name && t.IsCustom == custom && t.Namespace is not null))
                Reject("$.tool_choice", "namespaced_tool_choice_not_supported");
            var identity = new ConvertedToolIdentity(null, name, custom);
            if (!declared.Contains(identity)) Reject("$.tool_choice.name", custom ? "unknown_custom_tool" : "tool_choice_kind_mismatch");
            if (map.TryGetValue(identity, out var binding)) { choice["type"] = "function"; choice["name"] = binding.EncodedName; }
        }
        else if (type is "auto" or "none" or "any") Known(choice, "$.tool_choice", "type");
        else Reject("$.tool_choice", "custom_tool_choice_not_supported");
    }

    /// <summary>从原生调用项读取独立 namespace、名称和种类，null/空作用域不能伪装为根。</summary>
    internal static ConvertedToolIdentity ReadIdentity(JsonObject item, bool custom, LlmConversionOptions options)
    {
        var identity = new ConvertedToolIdentity(item.ContainsKey("namespace") ? Name(item["namespace"]) : null, Name(item["name"]), custom);
        ValidateEnabled(identity, options); return identity;
    }
    /// <summary>两个显式选项彼此独立，namespace 不能暗中启用 custom 封装。</summary>
    private static void ValidateEnabled(ConvertedToolIdentity identity, LlmConversionOptions options)
    {
        if (identity.Namespace is not null && !options.EnableNamespacedToolConversion) Reject("$.namespace", "namespace_conversion_not_enabled");
        if (identity.IsCustom && !options.EnableCustomToolConversion) Reject("$.tools", "custom_tool_conversion_not_enabled");
    }
    /// <summary>文本格式可逆；CFG/程序调用/延迟与异步语义不能被删除后假称等价。</summary>
    private static void ValidateCustom(JsonObject tool)
    {
        Known(tool, "$.tools[]", "type", "name", "description", "format");
        if (tool.ContainsKey("description")) _ = Text(tool["description"]);
        if (!tool.ContainsKey("format")) return;
        var format = Object(tool["format"]);
        if (Text(format["type"]) != "text") Reject("$.tools[].format", "custom_tool_format_not_supported");
        Known(format, "$.tools[].format", "type");
    }
    /// <summary>映射安全字段始终硬失败，不由普通损耗选项绕过。</summary>
    internal static void Known(JsonObject value, string path, params string[] fields)
    {
        foreach (var key in value.Select(pair => pair.Key)) if (!fields.Contains(key, StringComparer.Ordinal)) Reject(path + "." + key, "unsupported_custom_tool_field");
    }
    /// <summary>身份分量非空，保留大小写、标点及 Unicode，不猜测复合名称。</summary>
    internal static string Name(JsonNode? node) { var name = Text(node); ArgumentException.ThrowIfNullOrWhiteSpace(name); return name; }
    /// <summary>消息不回显原始参数、工具文本或凭据。</summary>
    internal static void Reject(string path, string code) => throw new LlmConversionException([new(code, path, "工具无法在当前请求上下文中保真转换。")]);
}
