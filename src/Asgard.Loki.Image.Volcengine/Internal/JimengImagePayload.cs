using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>冻结同步 Visual 原生字段，不注入 return_url、尺寸、默认开关或响应格式。</summary>
internal static class JimengImagePayload
{
    /// <summary>在工厂、时钟及首次等待前捕获实际请求字节。</summary>
    internal static byte[] Prepare(ImageGenerationRequest request, JimengImageOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); ArgumentNullException.ThrowIfNull(request.Parameters);
        JimengImageJson.Text(request.Model); JimengImageJson.Text(request.Prompt);
        var body = new JsonObject { ["req_key"] = options.RequestKey, ["prompt"] = request.Prompt };
        foreach (var field in request.Parameters)
        {
            JimengImageJson.Text(field.Key); JimengImageJson.Validate(field.Value);
            if (new[] { "req_key", "prompt", "model" }.Contains(field.Key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("扩展参数不能覆盖固定 req_key、公共路由或原始 prompt。");
            if (field.Key.Equals("stream", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Key != "stream" || field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("stream 必须使用原生布尔值及大小写。");
                if (field.Value.GetBoolean()) throw new NotSupportedException("CVProcess 图片操作只返回完整原生 JSON。");
            }
            body.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body); _ = JimengImageJson.Parse(bytes);
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("Visual 图片请求超过本地字节上限。");
        return bytes;
    }
}
