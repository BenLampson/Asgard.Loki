using System.Text.Json;

namespace Asgard.Loki.Image.Midjourney;

/// <summary>原生图片操作响应，不将输入上传 URL 或 seed 结果解释为普通任务 ID。</summary>
public class MidjourneyImageResponse
{
    internal MidjourneyImageResponse(JsonElement data, MidjourneyImageHttpResponse httpResponse) { Data = data.Clone(); HttpResponse = httpResponse; }
    /// <summary>完整原始 JSON，未知字段、空值、数值和顺序均保持。</summary>
    public JsonElement Data { get; }
    /// <summary>实际完整 HTTP 元数据及原始字节。</summary>
    public MidjourneyImageHttpResponse HttpResponse { get; }
    /// <summary>原生 code，仅对象响应提供，不从 HTTP 或终态推算。</summary>
    public long? Code => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("code", out var code) ? code.GetInt64() : null;
}
