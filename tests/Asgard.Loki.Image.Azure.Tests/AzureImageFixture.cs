using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>实现前提交的独立原生样本，身份/版本及媒体均为虚构。</summary>
internal static class AzureImageFixture
{
    internal static JsonElement Data()
    {
        using var stream = Resource("azure-images.json"); using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    internal static Stream Resource(string name) => typeof(AzureImageFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Image.Azure.Tests.Fixtures." + name)!;
    internal static byte[] Bytes(string name) { using var stream = Resource(name); using var copy = new MemoryStream(); stream.CopyTo(copy); return copy.ToArray(); }
    internal static JsonElement Part(string name) => Data().GetProperty(name);
    internal static AzureImageOptions Options(string? bodyModel = null, int requestLimit = 4 * 1024 * 1024, int responseLimit = 64 * 1024 * 1024,
        long uploadLimit = 50L * 1024 * 1024, string endpoint = "https://azure-image.example.invalid/prefix/", string deployment = "literal.image-deployment",
        string version = "fixture-version+literal", string key = "fixture-azure-image-key", string id = "azure-test", string client = "azure-image-test")
        => new() { Id = id, Endpoint = new(endpoint, UriKind.RelativeOrAbsolute), Deployment = deployment, ApiVersion = version, ApiKey = key, BodyModel = bodyModel,
            HttpClientName = client, MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit, MaxUploadBytes = uploadLimit };
    internal static ImageGenerationRequest Generation(int index = 0)
    {
        var data = Part("generation_requests")[index]; return new() { Model = "images", Prompt = data.GetProperty("prompt").GetString()!,
            Parameters = data.EnumerateObject().Where(field => field.Name is not ("prompt" or "model")).ToDictionary(field => field.Name, field => field.Value.Clone()) };
    }
    internal static ImageMultipartEditRequest Edit(int index, IReadOnlyList<Stream> images, Stream? mask = null)
    {
        var data = Part("multipart_edits")[index]; var imageData = data.GetProperty("images");
        ImageUpload Upload(JsonElement item, Stream source) => new() { Content = source, FileName = item.GetProperty("filename").GetString()!, ContentType = item.GetProperty("content_type").GetString() };
        return new() { Model = "images", Prompt = data.GetProperty("prompt").GetString()!,
            Images = images.Select((stream, position) => Upload(imageData[position], stream)).ToArray(),
            Mask = mask is null ? null : Upload(data.GetProperty("mask"), mask),
            Fields = data.GetProperty("fields").EnumerateArray().Select(field => new ImageMultipartField(field.GetProperty("name").GetString()!, field.GetProperty("value").GetString()!)).ToArray() };
    }
    internal static ILokiClient Build(IHttpClientFactory factory, bool editing = false, AzureImageOptions? options = null)
    {
        var configured = options ?? Options(); return new LokiClientBuilder().AddImage(editing
            ? new ImageOptions { EditRoutes = [new() { Model = "images", Targets = [new AzureImageEditAdapter(factory, configured)] }] }
            : new ImageOptions { Routes = [new() { Model = "images", Targets = [new AzureImageAdapter(factory, configured)] }] }).Build();
    }
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => Json(data.GetRawText(), status);
    internal static HttpResponseMessage Json(string data, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
}
