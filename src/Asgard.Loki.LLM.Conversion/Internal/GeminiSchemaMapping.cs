using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>将 Gemini typed Schema 显式翻译为 JSON Schema；不删除校验约束。</summary>
internal static class GeminiSchemaMapping
{
    /// <summary>递归保留类型、校验和注释；目标无法保证属性输出顺序时报告精确损耗。</summary>
    internal static JsonObject Read(JsonObject schema, ConversionDocument doc, string path, LlmProtocol target, int depth = 0)
    {
        if (depth > 64) Reject(path, "schema_too_deep");
        var type = Type(schema["type"], path + ".type");
        var result = new JsonObject { ["type"] = type };
        if (schema["nullable"] is { } nullable && nullable.GetValue<bool>() && type != "null")
            result["type"] = new JsonArray(type, "null");
        foreach (var (sourceKey, value) in schema)
        {
            var key = Canonical(sourceKey); var field = path + "." + sourceKey;
            if (sourceKey != key && schema.ContainsKey(key)) throw new ArgumentException($"{field} 与同名 JSON 字段冲突。");
            // ProtoJSON 普通字段 null 表示未设置，Value 注释的 null 则是原始值。
            if (value is null && key is not ("example" or "default"))
            {
                if (!Known(key)) Reject(field, "unsupported_schema_keyword");
                continue;
            }
            switch (key)
            {
                case "type": case "nullable": case "propertyOrdering": break;
                case "format": case "title": case "description": case "pattern":
                    result[key] = value!.GetValue<string>(); break;
                case "default": result[key] = value?.DeepClone(); break;
                case "example": result["examples"] = new JsonArray(value?.DeepClone()); break;
                case "minItems": case "maxItems": case "minProperties": case "maxProperties": case "minLength": case "maxLength":
                    result[key] = GeminiSchemaInteger.Read(value!, field); break;
                case "minimum": case "maximum": result[key] = Numeric(value!, field); break;
                case "enum": case "required":
                    var strings = Array(value);
                    foreach (var item in strings) _ = Text(item);
                    if (key == "enum" && strings.Count == 0) break;
                    if (key == "enum" && type != "string") throw new ArgumentException($"{field} 只支持 STRING 枚举。");
                    result[key] = strings.DeepClone(); break;
                case "properties":
                    var properties = new JsonObject();
                    foreach (var (name, child) in Object(value))
                        properties[name] = Read(Object(child), doc, field + "[" + JsonSerializer.Serialize(name) + "]", target, depth + 1);
                    result[key] = properties; break;
                case "items": result[key] = Read(Object(value), doc, field, target, depth + 1); break;
                case "anyOf":
                    var alternatives = new JsonArray();
                    foreach (var child in Array(value))
                        alternatives.Add(Read(Object(child), doc, field + "[" + alternatives.Count + "]", target, depth + 1));
                    if (alternatives.Count != 0) result[key] = alternatives;
                    break;
                default: Reject(field, "unsupported_schema_keyword"); break;
            }
        }
        ApplyOrdering(schema, result, doc, path, target);
        return result;
    }

    /// <summary>类型名和官方 protobuf 数字枚举一一对应；未知类型不得变成任意值。</summary>
    private static string Type(JsonNode? value, string path)
    {
        if (value is null) throw new ArgumentException($"{path} 是必填类型。");
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            var type = text.ToLowerInvariant();
            if (type is "string" or "number" or "integer" or "boolean" or "array" or "object" or "null") return type;
            Reject(path, "unsupported_schema_type");
        }
        return GeminiSchemaInteger.Read(value, path) switch
        {
            1 => "string", 2 => "number", 3 => "integer", 4 => "boolean", 5 => "array", 6 => "object", 7 => "null",
            _ => throw new LlmConversionException([new("unsupported_schema_type", path, "不支持未指定或未知的 Schema 类型。")])
        };
    }

    /// <summary>有限数值按原始 JSON 数字保留，不经浮点数重写精度。</summary>
    private static JsonNode Numeric(JsonNode value, string path)
    {
        var number = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? JsonNode.Parse(text) : value.DeepClone();
        if (number is not JsonValue numeric || numeric.GetValueKind() != JsonValueKind.Number)
            throw new ArgumentException($"{path} 必须是有限数值。");
        if (!JsonSerializer.SerializeToElement(number).TryGetDouble(out var finite) || !double.IsFinite(finite))
            throw new ArgumentException($"{path} 超出有限 double 范围。");
        return number;
    }

    /// <summary>OpenAI 输出顺序由 properties 顺序表达；其他目标不能伪称保证顺序。</summary>
    private static void ApplyOrdering(JsonObject schema, JsonObject result, ConversionDocument doc, string path, LlmProtocol target)
    {
        var orderingKey = schema.ContainsKey("propertyOrdering") ? "propertyOrdering" : "property_ordering";
        if (schema[orderingKey] is not { } value || Array(value).Count == 0) return;
        var names = Array(value).Select(Text).ToArray();
        var properties = result["properties"] as JsonObject ?? throw new ArgumentException($"{path}.propertyOrdering 缺少 properties。");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => !properties.ContainsKey(name)))
            throw new ArgumentException($"{path}.propertyOrdering 包含重复或未知的属性名。");
        if (names.Length != properties.Count || target is not (LlmProtocol.ChatCompletions or LlmProtocol.Responses))
            Loss(doc, path + "." + orderingKey, "schema_property_ordering_not_representable");
        var ordered = new JsonObject();
        foreach (var name in names) ordered[name] = properties[name]?.DeepClone();
        foreach (var (name, child) in properties) if (!ordered.ContainsKey(name)) ordered[name] = child?.DeepClone();
        result["properties"] = ordered;
    }

    /// <summary>未知关键字即使值为 null 也拒绝，不通过 AllowLoss 删除潜在校验约束。</summary>
    private static bool Known(string key) => key is "type" or "nullable" or "propertyOrdering" or "format" or "title" or "description" or "pattern"
        or "default" or "example" or "minItems" or "maxItems" or "minProperties" or "maxProperties" or "minLength" or "maxLength"
        or "minimum" or "maximum" or "enum" or "required" or "properties" or "items" or "anyOf";

    /// <summary>ProtoJSON 同时接受 lowerCamelCase 与原始 protobuf 字段名，重复别名不能掩盖约束。</summary>
    private static string Canonical(string key) => key switch
    {
        "min_items" => "minItems", "max_items" => "maxItems", "min_properties" => "minProperties", "max_properties" => "maxProperties",
        "min_length" => "minLength", "max_length" => "maxLength", "any_of" => "anyOf", "property_ordering" => "propertyOrdering", _ => key
    };

    /// <summary>不可识别的校验含义始终硬失败，防止放宽 Schema 后仍报告成功。</summary>
    private static void Reject(string path, string code) => throw new LlmConversionException([new(code, path, "该 typed Schema 约束无法安全转换。")]);
}
