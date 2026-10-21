using System.Text;
using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>完整原生编辑 SSE/请求样本，与非流式样本独立比对，始终通过本地 HTTP 替身执行。</summary>
internal static class EditSseFixture
{
    internal static string Body
    {
        get
        {
            var assembly = typeof(EditSseFixture).Assembly;
            using var resource = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(".edit-stream-response.sse", StringComparison.Ordinal)))!;
            using var reader = new StreamReader(resource); return reader.ReadToEnd();
        }
    }
    internal static ImageEditRequest Json(bool explicitTrue = false)
    {
        var request = EditFixture.Request(); request.Parameters["n"] = JsonSerializer.SerializeToElement(1); request.Parameters["partial_images"] = JsonSerializer.SerializeToElement(2);
        if (explicitTrue) request.Parameters["stream"] = JsonSerializer.SerializeToElement(true); return request;
    }
    internal static ImageMultipartEditRequest Multipart(Stream one, Stream two, Stream mask, bool explicitTrue = false)
    {
        var request = EditFixture.Multipart(one, two, mask);
        var fields = request.Fields.Where(f => f.Name != "stream").Append(new("partial_images", "2")).ToList();
        if (explicitTrue) fields.Add(new("stream", "true")); return request with { Fields = fields };
    }
    internal static IAsyncEnumerable<ImageEditEvent> Source(ILokiClient client, bool multipart, Stream one, Stream two, Stream mask, CancellationToken token)
        => multipart ? client.Image.StreamMultipartEditAsync(Multipart(one, two, mask), token) : client.Image.StreamEditAsync(Json(), token);
    internal static IAsyncEnumerable<ImageEditEvent> Source(OpenAiImageEditAdapter adapter, bool multipart, Stream one, Stream two, Stream mask, CancellationToken token)
        => multipart ? adapter.StreamMultipartEditAsync(Multipart(one, two, mask), token) : adapter.StreamEditAsync(Json(), token);
    internal static async Task<List<ImageEditEvent>> Collect(IAsyncEnumerable<ImageEditEvent> source)
    { List<ImageEditEvent> result = []; await foreach (var item in source) result.Add(item); return result; }
    internal static string Partial => "data: {\"type\":\"image_edit.partial_image\",\"partial_image_index\":0,\"b64_json\":\"AP+A\"}\n\n";
    internal static string Completed => "data: {\"type\":\"image_edit.completed\",\"b64_json\":\"AQIDBA==\"}\n\n";
    internal static HttpResponseMessage Response(string? body = null)
        => EditFixture.Response(new EditReadStream(Encoding.UTF8.GetBytes(body ?? Body)), "text/event-stream; charset=utf-8");
}
