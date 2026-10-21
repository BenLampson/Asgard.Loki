using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Ali.Internal;

/// <summary>原生根字段快照，只为明确入口添加固定模型及显式流标记。</summary>
internal static class AliImagePayload
{
    internal static AliNativeImageRequest Bridge(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); ArgumentNullException.ThrowIfNull(request.Parameters);
        AliImageJson.Text(request.Prompt);
        var root = new JsonObject { ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject
            { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["text"] = request.Prompt }) }) } };
        foreach (var field in request.Parameters)
        {
            AliImageJson.Text(field.Key); AliImageJson.Validate(field.Value);
            if (field.Key.Equals("input", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("便利入口不能覆盖 prompt 对应的 input，请使用原生入口。");
            root.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        }
        return new() { PublicModel = request.Model, Parameters = root };
    }
    internal static byte[] Prepare(AliNativeImageRequest request, AliImageOptions options, bool streaming, CancellationToken token, bool asyncTask = false)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicModel);
        AliImageJson.Text(request.PublicModel); ArgumentNullException.ThrowIfNull(request.Parameters); AliImageJson.Node(request.Parameters);
        var root = AliImageJson.Parse(JsonSerializer.SerializeToUtf8Bytes(request.Parameters));
        foreach (var field in root.EnumerateObject())
        {
            if (field.Name.Equals("model", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("原生请求不能覆盖固定 model。");
            if (new[] { "input", "parameters" }.Any(name => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && field.Name != name))
                throw new ArgumentException("已知阿里根字段不能使用大小写别名。");
        }
        if (!root.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) throw new ArgumentException("原生 input 必须为对象。");
        var copy = JsonNode.Parse(root.GetRawText())!.AsObject();
        if (copy.TryGetPropertyValue("parameters", out var value) && value is not JsonObject) throw new ArgumentException("原生 parameters 必须为对象。");
        var parameters = value as JsonObject ?? new JsonObject();
        foreach (var field in parameters)
            if (new[] { "stream", "enable_interleave" }.Any(name => field.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && field.Key != name))
                throw new ArgumentException("已知原生模式字段不能使用大小写别名。");
        var stream = Flag(parameters, "stream"); var interleave = Flag(parameters, "enable_interleave");
        if (streaming && stream == false || !streaming && stream == true) throw new ArgumentException("parameters.stream 与显式调用入口不一致。");
        if (!streaming && !asyncTask && interleave == true) throw new NotSupportedException("同步图文混排需要显式原生流入口。");
        if (streaming)
        {
            if (interleave != true) throw new ArgumentException("阿里原生图文流需明确 parameters.enable_interleave=true。");
            parameters["stream"] = true; copy["parameters"] = parameters;
        }
        copy["model"] = options.Model; var bytes = JsonSerializer.SerializeToUtf8Bytes(copy);
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("阿里图片请求超过本地字节上限。");
        return bytes;
    }
    private static bool? Flag(JsonObject value, string name)
    {
        if (!value.TryGetPropertyValue(name, out var flag)) return null;
        if (flag is not JsonValue scalar || !scalar.TryGetValue<bool>(out var result)) throw new ArgumentException(name + " 必须是布尔值。");
        return result;
    }
}
