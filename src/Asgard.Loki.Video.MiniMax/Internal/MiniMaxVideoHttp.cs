using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Video.MiniMax.Internal;

/// <summary>MiniMax 原生 HTTP/JSON 边界，原始字节交由独立基础设施读取。</summary>
internal static class MiniMaxVideoHttp
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    /// <summary>拒绝有损 Unicode，不修改标识或正文。</summary>
    internal static void Text(string value) => _ = Utf8.GetByteCount(value);
    /// <summary>任务标识作为一个原生参数传递，不接受可被规范化的路径跳转。</summary>
    internal static void TaskId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value); Text(value);
        if (value is "." or ".." || value.Any(char.IsControl)) throw new ArgumentException("MiniMax 任务标识无效。");
    }
    /// <summary>只调用一次，完整读取和释放调用资源，不重试、跟随结果链接或轮询。</summary>
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
    /// <summary>严格解析完整原生对象，未知成员也不能包含重复属性或损坏文本。</summary>
    internal static JsonElement Parse(byte[] bytes, string? contentType)
    {
        if (contentType is not null && (!MediaTypeHeaderValue.TryParse(contentType, out var header) || !string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("MiniMax 视频接口没有返回原生 JSON。");
        try
        {
            _ = Utf8.GetCharCount(bytes); using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MiniMax 原生结果必须是对象。");
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
        { throw new InvalidDataException("MiniMax 原生 JSON 或 UTF-8 编码无效。", error); }
    }
    /// <summary>原生必需文本不接受类型强制转换或空白占位。</summary>
    internal static string RequiredText(JsonElement value, string name)
        => value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
            ? text.GetString()! : throw new InvalidDataException("MiniMax 结果缺少有效 " + name + "。");
}
