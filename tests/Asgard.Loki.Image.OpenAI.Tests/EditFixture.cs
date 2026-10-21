using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>独立完整协议样本和显式公开 SDK 编辑入口，始终使用无效外网域名及本地替代处理器。</summary>
internal static class EditFixture
{
    internal static OpenAiImageOptions Options => new()
    {
        Id = "edit-target-one", Model = "native-image-model", ApiKey = "fixture-image-secret",
        BaseAddress = new("https://images.invalid/tenant/v1/"), Organization = "org-fixture", Project = "project-fixture",
        HttpClientName = "fixture-images"
    };
    internal static string Text(string name)
    {
        var assembly = typeof(EditFixture).Assembly;
        using var resource = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name + ".json", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(resource); return reader.ReadToEnd();
    }
    internal static JsonElement Json(string name) { using var document = JsonDocument.Parse(Text(name)); return document.RootElement.Clone(); }
    internal static ImageEditRequest Request() => JsonSerializer.Deserialize<ImageEditRequest>(Text("edit-json-input"))!;
    internal static ImageMultipartEditRequest Multipart(Stream first, Stream second, Stream mask, string kind = "rich") => new()
    {
        Model = "pictures", Prompt = "保留主体\nchange background",
        Images = kind == "minimal" ? [new() { Content = first, FileName = "first.png" }]
            : [new() { Content = first, FileName = "first.png", ContentType = "image/png" }, new() { Content = second, FileName = "second.webp", ContentType = "image/webp" }],
        Mask = kind == "rich" ? new() { Content = mask, FileName = "mask.png", ContentType = "image/png" } : null,
        Fields = kind == "minimal" ? [] : [new("stream", "false"), new("output_format", "webp"), new("output_compression", "75"),
            new("future[]", "null"), new("future[]", "false"), new("future[nested]", "{\"value\":null}"), new("empty", "")]
    };
    internal static HttpResponseMessage Response(Stream? stream = null, string? media = "application/json", long? length = null, int status = 200)
    {
        var content = new StreamContent(stream ?? new EditReadStream(Encoding.UTF8.GetBytes(Text("edit-json-response"))));
        if (media is not null) content.Headers.ContentType = MediaTypeHeaderValue.Parse(media);
        if (length is { } size) content.Headers.ContentLength = size;
        return new((HttpStatusCode)status) { Content = content };
    }
    internal static ILokiClient Build(IImageEditAdapter adapter) => new LokiClientBuilder().AddImage(new()
    { EditRoutes = [new() { Model = "pictures", Targets = [adapter] }] }).Build();
    internal static Task<ImageGenerationResult> Send(ILokiClient client, bool multipart, Stream one, Stream two, Stream mask, CancellationToken token)
        => multipart ? client.Image.EditMultipartAsync(Multipart(one, two, mask), token) : client.Image.EditAsync(Request(), token);
}
