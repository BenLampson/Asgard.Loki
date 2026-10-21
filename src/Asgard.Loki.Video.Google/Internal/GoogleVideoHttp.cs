using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Video.Google.Internal;

/// <summary>Gemini 视频的原生 HTTP/JSON 边界，字节传输委托给基础设施。</summary>
internal static class GoogleVideoHttp
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>严格检查文本，避免损坏 Unicode 改变身份或正文。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>校验原生路径段，不接受嵌入的转义路径、方法、查询或路径跳转。</summary>
    internal static void Segment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); Text(value);
        if (value is "." or ".." || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "/\\%?#:".Contains(c)))
            throw new ArgumentException("原生资源路径段无效。");
    }
    /// <summary>只发送一次原生任务请求，先完整读取并释放资源，再公开结果。</summary>
    internal static async Task<(HttpStatusCode Status, byte[] Body, string? ContentType)> SendAsync(IHttpClientFactory factory, string clientName,
        HttpRequestMessage request, int maximumBytes, CancellationToken cancellationToken)
    {
        (HttpStatusCode Status, byte[] Body, string? ContentType) reply;
        using (request)
        using (var client = factory.CreateClient(clientName))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            reply = (response.StatusCode, await HttpBodyReader.ReadAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false), response.Content.Headers.ContentType?.ToString());
        }
        cancellationToken.ThrowIfCancellationRequested(); return reply;
    }
    /// <summary>严格保留原生 JSON，包括未知成员；重复字段、无效 UTF-8 或字符串均明确失败。</summary>
    internal static JsonElement Parse(byte[] bytes, string? contentType)
    {
        if (contentType is not null && (!MediaTypeHeaderValue.TryParse(contentType, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Gemini 视频接口未返回原生 JSON。");
        try
        {
            _ = Utf8.GetCharCount(bytes); using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gemini 操作必须是原生 JSON 对象。");
            var pending = new Stack<JsonElement>(); pending.Push(root);
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var member in value.EnumerateObject()) { _ = member.Name; pending.Push(member.Value); }
            }
            return root.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("Gemini 原生 JSON 或 UTF-8 编码无效。", error); }
    }
}
