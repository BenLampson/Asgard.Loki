using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>仅对已验证相同的 xAI 原生完整生成协议复用现有封装，不宣称编辑或流式兼容。</summary>
public sealed class XaiImageCompatibilityTests
{
    /// <summary>URL/Base64 完整原生请求、结果、MIME、修订及精确用量字段不丢失。</summary>
    [Theory]
    [InlineData(0, "url")] [InlineData(1, "base64")]
    public async Task NativeXaiJsonMatchesCompleteIndependentVectors(int index, string name)
    {
        var expected = Vector("requests")[index]; var response = Vector(name);
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://xai-images.example.invalid/proxy/v1/images/generations", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-xai-image-key", message.Headers.Authorization.Parameter);
            Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
            using var json = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(expected, json.RootElement));
            return Reply(response);
        });
        using var factory = new EditTestFactory(handler); await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, Options()));
        var request = new ImageGenerationRequest { Model = "pictures", Prompt = expected.GetProperty("prompt").GetString()!,
            Parameters = expected.EnumerateObject().Where(field => field.Name is not ("model" or "prompt")).ToDictionary(field => field.Name, field => field.Value.Clone()) };
        var result = await client.Image.GenerateAsync(request, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(response, result.Data)); var image = Assert.Single(result.Images); Assert.True(image.Data.TryGetProperty("mime_type", out _));
        if (name == "url") { Assert.Equal("https://result.example.invalid/a%2Fb.jpg?sig=A%2BB", image.Url); Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("cost_in_usd_ticks").GetRawText()); }
        else Assert.Equal(new byte[] { 1, 2, 3 }, image.Decode().ToArray());
        Assert.Equal(new ImageResultMetadata("pictures", "native-xai-fixture", "xai-compatible-generation"), result.Metadata);
        Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>普通生成不自动注入 n、格式、质量或 stream 默认字段。</summary>
    [Fact]
    public async Task NativeXaiMinimalRequestDoesNotAcquireDefaults()
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var json = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); using var expected = JsonDocument.Parse("{\"model\":\"native-xai-fixture\",\"prompt\":\"a cat\"}");
            Assert.True(JsonElement.DeepEquals(expected.RootElement, json.RootElement)); return Reply(Vector("url"));
        });
        using var factory = new EditTestFactory(handler);
        await new OpenAiImageAdapter(factory, Options()).GenerateAsync(new() { Model = "pictures", Prompt = "a cat" }, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>实际 xAI 错误正文和 HTTP 状态完整保留，生成不重试。</summary>
    [Theory]
    [InlineData(200)] [InlineData(400)]
    public async Task NativeXaiErrorsKeepOriginalBodyWithoutRetry(int status)
    {
        var expected = Vector("error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(expected, (HttpStatusCode)status))); using var factory = new EditTestFactory(handler);
        var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new OpenAiImageAdapter(factory, Options()).GenerateAsync(new() { Model = "pictures", Prompt = "a cat" }, TestContext.Current.CancellationToken));
        Assert.Equal(status, error.StatusCode); Assert.Equal(expected.GetRawText(), error.ResponseBody); Assert.Equal(1, handler.Calls);
    }
    /// <summary>虚构的明确 xAI 根地址/模型/凭据，不依赖环境或真实模型。</summary>
    private static OpenAiImageOptions Options() => new() { Id = "xai-compatible-generation", Model = "native-xai-fixture", ApiKey = "fixture-xai-image-key", BaseAddress = new("https://xai-images.example.invalid/proxy/v1/"), HttpClientName = "xai-image-tests" };
    /// <summary>独立原生向量仅从嵌入资源读取，不经过生产编码器。</summary>
    private static JsonElement Vector(string name)
    {
        using var stream = typeof(XaiImageCompatibilityTests).Assembly.GetManifestResourceStream("Asgard.Loki.Image.OpenAI.Tests.Fixtures.xai-generation.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.GetProperty(name).Clone();
    }
    /// <summary>离线原始响应，不下载图片。</summary>
    private static HttpResponseMessage Reply(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
}
