using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Internal;

/// <summary>在任何选择器、工厂或异步边界之前冻结输入，拒绝无效 Unicode 和重复字段。</summary>
internal static class LokiImageSnapshots
{
    internal static LokiImageGenerationRequest Request(LokiImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); Text(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); Text(request.Prompt);
        if (request.Count is <= 0 || request.Steps is <= 0 || request.Size is { Width: <= 0 } or { Height: <= 0 }
            || request.GuidanceScale is { } scale && (!double.IsFinite(scale) || scale < 0))
            throw new ArgumentException("数量、尺寸、步数或引导系数无效。", nameof(request));
        foreach (var value in new[] { request.AspectRatio, request.Quality, request.OutputFormat, request.Background, request.NegativePrompt })
            if (value is not null) { ArgumentException.ThrowIfNullOrWhiteSpace(value); Text(value); }
        return request with { NativeParameters = Parameters(request.NativeParameters, false) };
    }

    internal static LokiNativeImageGenerationRequest Native(LokiNativeImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); Text(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProviderId); Text(request.ProviderId);
        return request with { Parameters = Parameters(request.Parameters, true) };
    }

    internal static JsonObject Parameters(JsonObject value, bool native)
    {
        ArgumentNullException.ThrowIfNull(value);
        // 先检查 DOM 中的原始字符串，不能依赖序列化器替换无效代理字符。
        Node(value);
        var snapshot = (JsonObject)value.DeepClone();
        foreach (var field in snapshot)
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase)
                || field.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)
                || !native && field.Key.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("参数不能覆盖固定 model、方法控制的 stream 或统一 prompt。");
        return snapshot;
    }

    internal static void Node(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in obj)
            { Text(field.Key); if (!names.Add(field.Key)) throw new ArgumentException("图片 JSON 含重复字段。"); Node(field.Value); }
        }
        else if (node is JsonArray array) foreach (var item in array) Node(item);
        else if (node is JsonValue value)
        {
            if (value.TryGetValue<JsonElement>(out var element)) Element(element);
            else if (value.TryGetValue<string>(out var text)) Text(text);
            else Element(JsonSerializer.SerializeToElement(value));
        }
    }

    internal static void Element(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("图片参数不能是 Undefined。");
        if (element.ValueKind == JsonValueKind.String) Text(element.GetString()!);
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Element(item);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            { Text(field.Name); if (!names.Add(field.Name)) throw new ArgumentException("图片 JSON 含重复字段。"); Element(field.Value); }
        }
    }

    internal static void Text(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsSurrogate(value[index])
                && (!char.IsHighSurrogate(value[index]) || index + 1 == value.Length || !char.IsLowSurrogate(value[++index])))
                throw new ArgumentException("图片参数包含无效 Unicode。");
    }

    internal static Dictionary<string, JsonElement> Values(LokiImageGenerationRequest request)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Add(nameof(request.Count), request.Count); Add(nameof(request.Size), request.Size?.ToString());
        Add(nameof(request.AspectRatio), request.AspectRatio); Add(nameof(request.Quality), request.Quality);
        Add(nameof(request.OutputFormat), request.OutputFormat); Add(nameof(request.Background), request.Background);
        Add(nameof(request.Seed), request.Seed); Add(nameof(request.NegativePrompt), request.NegativePrompt);
        Add(nameof(request.Steps), request.Steps); Add(nameof(request.GuidanceScale), request.GuidanceScale);
        Add(nameof(request.Watermark), request.Watermark);
        return values;
        void Add<T>(string name, T value) { if (value is not null) values.Add(name, JsonSerializer.SerializeToElement(value)); }
    }
}
