using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>生成请求在方法调用时建立独立原生快照，不覆盖显式传输开关或丢弃扩展字段。</summary>
internal static class OpenAiImageGenerationPayload
{
    /// <summary>调用者公开路由与上游实际模型分开；原生未知 JSON 不通过中间浮点/文本模型转换。</summary>
    internal static ImageGenerationRequest Snapshot(ImageGenerationRequest request, bool streaming, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt); ArgumentNullException.ThrowIfNull(request.Parameters);
        _ = new UTF8Encoding(false, true).GetByteCount(request.Prompt);
        if (request.Parameters.ContainsKey("model") || request.Parameters.ContainsKey("prompt")) throw new ArgumentException("扩展字段不能覆盖绑定的生成模型/提示词。");
        foreach (var pair in request.Parameters)
        {
            _ = new UTF8Encoding(false, true).GetByteCount(pair.Key);
            if (pair.Value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("生成参数不能含 Undefined。"); ImageNativeJson.Unique(pair.Value);
        }
        var fields = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (fields.TryGetValue("stream", out var value))
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("原生 stream 必须为布尔值。");
            if (streaming && value.ValueKind == JsonValueKind.False) throw new ArgumentException("流式生成不能覆盖显式 stream=false。");
            if (!streaming && value.ValueKind == JsonValueKind.True) throw new NotSupportedException("stream=true 必须使用原生生成流式入口。");
        }
        if (streaming && !fields.ContainsKey("stream")) fields.Add("stream", JsonSerializer.SerializeToElement(true));
        return request with { Parameters = fields };
    }
    /// <summary>绑定实际模型并原样写出其余字段，不注入原生非流式默认值。</summary>
    internal static HttpContent Create(ImageGenerationRequest request, string model)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("model", model); writer.WriteString("prompt", request.Prompt);
            foreach (var pair in request.Parameters) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); } writer.WriteEndObject();
        }
        var content = new ByteArrayContent(bytes.ToArray()); content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return content;
    }
}
