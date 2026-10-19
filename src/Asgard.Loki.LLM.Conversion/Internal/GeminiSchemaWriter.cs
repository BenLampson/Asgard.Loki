using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>显式输出 Gemini typed Schema；无法承载的校验约束始终失败。</summary>
internal static class GeminiSchemaWriter
{
    /// <summary>转换可表达的类型、约束与注释，不清理关键字或改写为描述。</summary>
    internal static JsonObject Write(JsonObject schema, ConversionDocument doc, string path, bool ordered, int depth = 0)
    {
        if (depth > 64) Reject(path, "schema_too_deep");
        var (type, nullable) = Type(schema["type"], path + ".type");
        var result = new JsonObject { ["type"] = type.ToUpperInvariant() };
        if (nullable) result["nullable"] = true;
        foreach (var (key, value) in schema)
        {
            var field = path + "." + key;
            switch (key)
            {
                case "type": break;
                case "format": case "title": case "description": case "pattern": result[key] = Text(value); break;
                case "default": result[key] = value?.DeepClone(); break;
                case "additionalProperties":
                    // 原生 typed 对象默认开放；只有明确 true 与该语义等价，false/子 Schema 不可丢弃。
                    if (value is not JsonValue open || !open.TryGetValue<bool>(out var allowed) || !allowed)
                        Reject(field, "schema_keyword_not_representable");
                    break;
                case "examples":
                    var examples = Array(value);
                    if (examples.Count > 1) Loss(doc, field, "schema_examples_not_representable");
                    if (examples.Count != 0) result["example"] = examples[0]?.DeepClone();
                    break;
                case "minItems": case "maxItems": case "minProperties": case "maxProperties": case "minLength": case "maxLength":
                    if (value is not JsonValue count || count.GetValueKind() != JsonValueKind.Number)
                        throw new ArgumentException($"{field} 必须是 JSON 整数。");
                    result[key] = GeminiSchemaInteger.Read(value, field).ToString(CultureInfo.InvariantCulture); break;
                case "minimum": case "maximum": result[key] = GeminiSchemaNumber.Write(value, field); break;
                case "enum":
                    var values = Array(value);
                    if (type != "string" || values.Count == 0 || values.Any(item => item is not JsonValue itemValue || itemValue.GetValueKind() != JsonValueKind.String))
                        Reject(field, "schema_enum_not_representable");
                    result[key] = values.DeepClone(); break;
                case "required":
                    var required = Array(value);
                    foreach (var name in required) _ = Text(name);
                    result[key] = required.DeepClone(); break;
                case "properties":
                    var properties = new JsonObject();
                    foreach (var (name, child) in Object(value))
                        properties[name] = Write(Object(child), doc, field + "[" + JsonSerializer.Serialize(name) + "]", ordered, depth + 1);
                    result[key] = properties;
                    if (ordered && properties.Count != 0) result["propertyOrdering"] = new JsonArray(properties.Select(pair => (JsonNode?)JsonValue.Create(pair.Key)).ToArray());
                    break;
                case "items": result[key] = Write(Object(value), doc, field, ordered, depth + 1); break;
                case "anyOf":
                    var alternatives = new JsonArray();
                    foreach (var child in Array(value)) alternatives.Add(Write(Object(child), doc, field + "[" + alternatives.Count + "]", ordered, depth + 1));
                    if (alternatives.Count == 0) throw new ArgumentException($"{field} 不能为空。");
                    result[key] = alternatives; break;
                default: Reject(field, "schema_keyword_not_representable"); break;
            }
        }
        return result;
    }

    /// <summary>只接受单一类型，或一个类型加 null；不能把多类型联合压成第一项。</summary>
    private static (string Type, bool Nullable) Type(JsonNode? value, string path)
    {
        var types = value is JsonArray array ? array.Select(Text).ToArray() : [Text(value)];
        if (types.Length == 0 || types.Distinct(StringComparer.Ordinal).Count() != types.Length)
            throw new ArgumentException($"{path} 必须包含不重复的显式类型。");
        var nonNull = types.Where(type => type != "null").ToArray();
        if (nonNull.Length > 1) Reject(path, "schema_union_not_representable");
        var type = nonNull.SingleOrDefault() ?? "null";
        if (type is not ("string" or "number" or "integer" or "boolean" or "array" or "object" or "null")) Reject(path, "unsupported_schema_type");
        return (type, types.Length > 1 && types.Contains("null", StringComparer.Ordinal));
    }

    /// <summary>损耗同意不允许删除校验约束后产生一个更宽的 Schema。</summary>
    private static void Reject(string path, string code) => throw new LlmConversionException([new(code, path, "该 JSON Schema 校验无法保真表示为 typed Schema。")]);
}
