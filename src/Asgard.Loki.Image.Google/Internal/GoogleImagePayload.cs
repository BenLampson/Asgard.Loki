using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Google.Internal;

/// <summary>完整原生图像输入快照，不注入模型目录、图像尺寸或输出模态默认值。</summary>
internal static class GoogleImagePayload
{
    internal static GoogleNativeImageRequest Bridge(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); ArgumentNullException.ThrowIfNull(request.Parameters);
        GoogleImageJson.Text(request.Prompt);
        var root = new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Prompt }) }) };
        foreach (var field in request.Parameters)
        {
            GoogleImageJson.Text(field.Key); GoogleImageJson.Validate(field.Value);
            if (field.Key.Equals("contents", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("便利入口不能覆盖原始 prompt，请使用原生 contents 入口。");
            root.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        }
        return new() { PublicModel = request.Model, Parameters = root };
    }
    internal static byte[] Prepare(GoogleNativeImageRequest request, GoogleImageOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicModel);
        GoogleImageJson.Text(request.PublicModel); ArgumentNullException.ThrowIfNull(request.Parameters); GoogleImageJson.Node(request.Parameters);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request.Parameters); var root = GoogleImageJson.Parse(bytes);
        if (root.EnumerateObject().Any(field => field.Name.Equals("model", StringComparison.OrdinalIgnoreCase) || field.Name.Equals("stream", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Gemini model 和 stream 由固定路径/显式方法确定，正文不能覆盖。");
        if (!root.TryGetProperty("contents", out var contents) || contents.ValueKind != JsonValueKind.Array || contents.GetArrayLength() == 0)
            throw new ArgumentException("Gemini 原生 contents 必须为非空数组。");
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("Gemini 图片请求超过本地字节上限。");
        return bytes;
    }
}
