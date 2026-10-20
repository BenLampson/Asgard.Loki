using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Video.Volcengine.Internal;

/// <summary>供应商内部有界 HTTP/JSON 工具，不解析任务状态或启动重试。</summary>
internal static class ArkVideoHttp
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>仅接受真正的 JSON 媒体类型，不能把同名前缀的其他类型当作 JSON。</summary>
    internal static bool IsJson(string? contentType) => contentType is null || MediaTypeHeaderValue.TryParse(contentType, out var header)
        && string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    /// <summary>验证绑定文本及路径不会经由编码替代改变身份。</summary>
    internal static void Text(string text) => _ = Utf8.GetByteCount(text);
    /// <summary>在返回原文之前等待完整 EOF、声明长度、所有资源释放及最终取消检查。</summary>
    internal static async Task<(HttpStatusCode Status, byte[] Body, string? ContentType)> SendAsync(IHttpClientFactory factory,
        string name, HttpRequestMessage request, int maximumBytes, CancellationToken cancellationToken)
    {
        (HttpStatusCode Status, byte[] Body, string? ContentType) result;
        using (request)
        using (var client = factory.CreateClient(name))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            var expected = response.Content.Headers.ContentLength;
            if (expected > maximumBytes) throw new InvalidDataException("方舟正文的声明长度超过显式字节上限。");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new ArrayBufferWriter<byte>(); var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    var length = (long)bytes.WrittenCount + count;
                    if (length > maximumBytes || expected is { } declared && length > declared) throw new InvalidDataException("方舟正文超过字节上限或声明长度。");
                    bytes.Write(buffer.AsSpan(0, count));
                }
                if (expected is { } size && bytes.WrittenCount != size) throw new InvalidDataException("方舟正文未达到声明长度。");
                result = (response.StatusCode, bytes.WrittenMemory.ToArray(), response.Content.Headers.ContentType?.ToString());
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>严格检查编码、重复属性和字符串转义，不忽略未知成员。</summary>
    internal static JsonElement Parse(byte[] bytes)
    {
        try
        {
            _ = Utf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("方舟结果必须是原生 JSON 对象。");
            var pending = new Stack<JsonElement>(); pending.Push(document.RootElement);
            while (pending.TryPop(out var value))
            {
                if (value.ValueKind == JsonValueKind.String) _ = value.GetString();
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) pending.Push(child);
                else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) { _ = property.Name; pending.Push(property.Value); }
            }
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException)
        { throw new InvalidDataException("方舟原生 JSON 或 UTF-8 编码无效。", error); }
    }
    /// <summary>任务标识和原生状态必须是原始非空文本，不强制字符串化其他类型。</summary>
    internal static string RequiredText(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw new InvalidDataException("方舟结果缺少有效 " + name + "。");
}
