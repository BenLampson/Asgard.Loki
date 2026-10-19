using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image.Tests;

/// <summary>独立原生 JSON/表单及结果样本；不发起 HTTP、下载 URL 或处理真实像素。</summary>
internal static class ImageEditFixture
{
    internal static JsonElement Golden(string name)
    {
        using var source = typeof(ImageEditFixture).Assembly.GetManifestResourceStream($"Asgard.Loki.Image.Tests.Fixtures.{name}.json")!;
        using var document = JsonDocument.Parse(source); return document.RootElement.Clone();
    }
    internal static ImageEditRequest Json() => JsonSerializer.Deserialize<ImageEditRequest>(Golden("edit-native-request").GetRawText())!;
    internal static ImageMultipartEditRequest Multipart(Stream one, Stream two, Stream mask) => new()
    {
        Model = "pictures", Prompt = "保留图层\nedit",
        Images = [new() { Content = one, FileName = "一.png", ContentType = "image/png" }, new() { Content = two, FileName = "two.webp" }],
        Mask = new() { Content = mask, FileName = "mask.png", ContentType = "image/png" },
        Fields = [new("output_format", "webp"), new("future[]", "null"), new("future[]", "false"), new("nested[key]", "{\"x\":null}"), new("empty", "")]
    };
    internal static ILokiClient Build(params IImageEditAdapter[] adapters) => new LokiClientBuilder().AddImage(new()
    { EditRoutes = [new() { Model = "pictures", Targets = adapters }] }).Build();
    internal static ImageEditEvent Event(bool done) => new(JsonSerializer.SerializeToElement(new
    { type = done ? "fixture.edit.completed" : "fixture.edit.partial", b64_json = "AQID", partial_image_index = 0, future = new object?[] { null, false, "原始" } }),
        done, new("pictures", "native-image-model", "edit-one"), "fixture.event", "wire-id", 42);
}
