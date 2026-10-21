using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Video.Ali.Internal;

/// <summary>供应商自身的 HTTP/JSON 边界，底层字节限制委托给独立基础设施。</summary>
internal static class AliVideoHttp
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝不能无损编码的目标文本，避免模型或凭据身份变化。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>读取完整正文并等待全部调用资源释放，不重试或下载媒体。</summary>
    internal static async Task<(HttpStatusCode Status, byte[] Body, string? ContentType)> SendAsync(IHttpClientFactory factory, string clientName,
        HttpRequestMessage request, int maximumBytes, CancellationToken cancellationToken)
    {
        (HttpStatusCode Status, byte[] Body, string? ContentType) reply;
        using (request)
        using (var client = factory.CreateClient(clientName))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            var body = await HttpBodyReader.ReadAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
            reply = (response.StatusCode, body, response.Content.Headers.ContentType?.ToString());
        }
        cancellationToken.ThrowIfCancellationRequested(); return reply;
    }
    /// <summary>严格解码原生对象，未知成员也不能藏有损坏字符串或重复属性。</summary>
    internal static JsonElement Parse(byte[] bytes, string? contentType)
    {
        if (contentType is not null && (!MediaTypeHeaderValue.TryParse(contentType, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("万相任务接口未返回原生 JSON。");
        try
        {
            _ = Utf8.GetCharCount(bytes); using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("万相任务结果必须是原生 JSON 对象。");
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
        { throw new InvalidDataException("万相原生 JSON 或 UTF-8 编码无效。", error); }
    }
    /// <summary>协议字段只能是非空原生文本，不强制字符串化其他类型。</summary>
    internal static string RequiredText(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw new InvalidDataException("万相任务结果缺少有效 " + name + "。");
}
