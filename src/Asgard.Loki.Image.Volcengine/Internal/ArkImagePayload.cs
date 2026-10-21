using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>原生参数整体快照，不改写提示词、引用、组图/图层设置，也不注入模型目录默认参数。</summary>
internal static class ArkImagePayload
{
    /// <summary>便利接口只加入明确提示词，实际模型始终由完整目标绑定。</summary>
    internal static VolcengineNativeImageRequest Bridge(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); ArgumentNullException.ThrowIfNull(request.Parameters);
        ArkImageJson.Text(request.Prompt); var parameters = new JsonObject { ["prompt"] = request.Prompt };
        foreach (var field in request.Parameters)
        {
            ArkImageJson.Text(field.Key); ArkImageJson.Validate(field.Value);
            if (field.Key.Equals("prompt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("扩展不能覆盖原始 prompt。");
            parameters.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        }
        return new() { PublicModel = request.Model, Parameters = parameters };
    }
    /// <summary>直接原生入口允许合法图层拆分省略 prompt，不把其改写为虚构文本。</summary>
    internal static ArkImageRequest Prepare(VolcengineNativeImageRequest request, VolcengineImageOptions options, bool streaming, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicModel);
        ArkImageJson.Text(request.PublicModel); ArgumentNullException.ThrowIfNull(request.Parameters); ArkImageJson.Node(request.Parameters);
        var root = ArkImageJson.Parse(JsonSerializer.SerializeToUtf8Bytes(request.Parameters));
        foreach (var member in root.EnumerateObject())
        {
            if (member.Name.Equals("model", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("原生参数不能覆盖完整目标 model。");
            foreach (var name in new[] { "prompt", "image", "stream", "layer_decomposition", "sequential_image_generation" })
                if (member.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && member.Name != name) throw new ArgumentException("已知方舟图片字段不能使用大小写别名。");
        }
        var layered = false;
        if (root.TryGetProperty("layer_decomposition", out var layer))
        {
            if (layer.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("layer_decomposition 必须是布尔值。");
            layered = layer.GetBoolean();
        }
        if (root.TryGetProperty("prompt", out var prompt))
        {
            if (prompt.ValueKind != JsonValueKind.String || !layered && string.IsNullOrWhiteSpace(prompt.GetString())) throw new ArgumentException("方舟图片 prompt 必须为原生文本。");
        }
        else if (!layered) throw new ArgumentException("普通方舟图片生成需要 prompt。");
        var references = 0;
        if (root.TryGetProperty("image", out var images))
        {
            var entries = images.ValueKind == JsonValueKind.Array ? images.EnumerateArray().ToArray() : new[] { images };
            foreach (var image in entries)
            {
                if (image.ValueKind != JsonValueKind.String) throw new ArgumentException("方舟 image 必须是原生 URL/data-URI 字符串或其数组。");
                Reference(image.GetString()!); references++;
            }
        }
        if (layered && references != 1) throw new ArgumentException("图层拆分需要恰好一个原生参考图。");
        if (root.TryGetProperty("stream", out var stream))
        {
            if (stream.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("原生 stream 必须是布尔值。");
            if (streaming && !stream.GetBoolean()) throw new ArgumentException("显式流入口不能覆盖 stream=false。");
            if (!streaming && stream.GetBoolean()) throw new NotSupportedException("stream=true 必须使用原生流式入口。");
        }
        var grouped = root.TryGetProperty("sequential_image_generation", out var group) && group.ValueKind == JsonValueKind.String && group.GetString() == "auto";
        var copy = JsonNode.Parse(root.GetRawText())!.AsObject(); copy["model"] = options.Model;
        if (streaming && !copy.ContainsKey("stream")) copy["stream"] = true;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(copy);
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("方舟图片原生请求超过本地字节上限。");
        return new(request.PublicModel, bytes, grouped);
    }
    /// <summary>检查引用形状但不下载、解码或规范化签名地址，也不接受中转站文件占位符。</summary>
    private static void Reference(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0
            && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl)) return;
        if (value.StartsWith("data:image/", StringComparison.Ordinal))
        {
            var separator = value.IndexOf(";base64,", StringComparison.Ordinal);
            if (separator > 11 && value[(separator + 8)..].Length > 0
                && value[11..separator].All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '+' or '-')
                && value[(separator + 8)..].All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=')) return;
        }
        throw new ArgumentException("方舟图片引用必须是原生 HTTP(S) URL 或小写格式的 Base64 data URI。");
    }
}
