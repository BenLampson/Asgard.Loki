using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Image.Zhipu.Internal;

/// <summary>原生 prompt 和扩展根字段快照，不计算尺寸、格式、默认值或计数。</summary>
internal static class ZhipuImagePayload
{
    /// <summary>创建 HTTP/首次等待之前冻结完整原生请求，模型只由固定目标提供。</summary>
    internal static byte[] Prepare(ImageGenerationRequest request, ZhipuImageOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); ArgumentNullException.ThrowIfNull(request.Parameters); ZhipuImageJson.Text(request.Model); ZhipuImageJson.Text(request.Prompt);
        var body = new JsonObject { ["model"] = options.Model, ["prompt"] = request.Prompt };
        foreach (var field in request.Parameters)
        {
            ZhipuImageJson.Text(field.Key); ZhipuImageJson.Validate(field.Value);
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || field.Key.Equals("prompt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("扩展参数不能覆盖固定模型或原始 prompt。");
            if (field.Key.Equals("stream", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Key != "stream" || field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("stream 必须为原生布尔值且不能使用大小写别名。");
                if (field.Value.GetBoolean()) throw new NotSupportedException("Zhipu 图片入口只有完整原生 JSON，不支持 stream=true。");
            }
            body.Add(field.Key, JsonNode.Parse(field.Value.GetRawText()));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body); _ = ZhipuImageJson.Parse(bytes);
        if (bytes.Length > options.MaxRequestBytes) throw new ArgumentException("Zhipu 图片请求超过本地字节上限。");
        return bytes;
    }
}
