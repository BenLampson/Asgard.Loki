using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>实现前提交的独立原生 SSE/请求金样及公开入口适配，不使用生产解析器造期望值。</summary>
internal static class AzureImageStreamFixture
{
    internal static JsonElement Data()
    {
        using var source = AzureImageFixture.Resource("azure-image-streams.json"); using var document = JsonDocument.Parse(source); return document.RootElement.Clone();
    }
    internal static JsonElement Case(bool editing, int count = 1) => Data().GetProperty("streams").EnumerateArray()
        .Single(item => item.GetProperty("operation").GetString() == (editing ? "edit" : "generation") && item.GetProperty("count").GetInt32() == count);
    internal static string Wire(bool editing, int count = 1) => Encoding.UTF8.GetString(AzureImageFixture.Bytes(Case(editing, count).GetProperty("wire").GetString()!));
    internal static ImageGenerationRequest Generation(int count = 1)
    {
        var source = Data().GetProperty("generation_requests")[count - 1];
        return new() { Model = "images", Prompt = source.GetProperty("prompt").GetString()!,
            Parameters = source.EnumerateObject().Where(field => field.Name != "prompt").ToDictionary(field => field.Name, field => field.Value.Clone()) };
    }
    internal static ImageMultipartEditRequest Edit(int count, IReadOnlyList<Stream> images, Stream? mask = null)
    {
        var source = Data().GetProperty("edit_requests")[count - 1];
        return AzureImageFixture.Edit(count - 1, images, mask) with
        { Fields = source.GetProperty("fields").EnumerateArray().Select(field => new ImageMultipartField(field.GetProperty("name").GetString()!, field.GetProperty("value").GetString()!)).ToArray() };
    }
    internal static HttpResponseMessage Response(Stream source) => new(HttpStatusCode.OK)
    { Content = new StreamContent(source) { Headers = { ContentType = new("text/event-stream") { CharSet = "utf-8" } } } };
    internal static HttpResponseMessage Response(string wire) => Response(new AzureImageReadStream(Encoding.UTF8.GetBytes(wire)));
    internal static string Frame(JsonElement data, string? eventName = null)
        => (eventName is null ? "" : "event: " + eventName + "\r\n")
            + string.Join("\r\n", data.GetRawText().Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => "data: " + line)) + "\r\n\r\n";
    internal static IAsyncEnumerable<AzureImageStreamEvent> Read(ILokiClient client, bool editing, int count, IReadOnlyList<Stream> images, Stream? mask, CancellationToken token)
        => editing ? Observe(client.Image.StreamMultipartEditAsync(Edit(count, images, mask), token)) : Observe(client.Image.StreamAsync(Generation(count), token));
    internal static async IAsyncEnumerable<AzureImageStreamEvent> Observe(IAsyncEnumerable<ImageEditEvent> source)
    {
        await foreach (var item in source) yield return new(item.Data, item.IsCompleted, item.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
    }
    internal static async IAsyncEnumerable<AzureImageStreamEvent> Observe(IAsyncEnumerable<ImageGenerationEvent> source)
    {
        await foreach (var item in source) yield return new(item.Data, null, item.Metadata, item.EventName, item.EventId, item.RetryMilliseconds);
    }
}
