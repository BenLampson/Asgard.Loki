using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Rerank.Tests;

/// <summary>核心原生数组快照和兼容的错误字节入口不解释供应商业务结构。</summary>
public sealed class NativeArrayAndErrorTests
{
    /// <summary>原生数组不包裹为对象，原始顺序和精确分数在文档释放后仍独立。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"index":2,"score":4.123456789012345678901,"future":null},{"index":0,"score":-2.5}]""")]
    public void NativeArrayIsIndependentWithoutWrapping(string json)
    {
        var source = JsonDocument.Parse(json); var result = new RerankResult(source.RootElement, new("public", "actual", "native")); source.Dispose();
        using var expected = JsonDocument.Parse(json); Assert.Equal(JsonValueKind.Array, result.Data.ValueKind); Assert.True(JsonElement.DeepEquals(expected.RootElement, result.Data));
    }
    /// <summary>公共路由允许供应商返回原生数组，核心不生成 results/usage 或改变目标身份。</summary>
    [Fact]
    public async Task PublicRoutePreservesSupplierNativeArray()
    {
        using var source = JsonDocument.Parse("""[{"index":1,"score":0},{"index":0,"score":-1}]""");
        using var adapter = new StubRerankAdapter("native", (_, _) => Task.FromResult(new RerankResult(source.RootElement, new("public", "actual", "native"))));
        await using var client = new LokiClientBuilder().AddRerank(new() { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
        var result = await client.Rerank.RerankAsync(new() { Model = "public" }, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(source.RootElement, result.Data)); Assert.Equal("actual", result.Metadata.UpstreamModel);
    }
    /// <summary>旧文本入口和属性保留，原始类型未知时不伪造媒体类型。</summary>
    [Fact]
    public void LegacyTextErrorKeepsExistingContract()
    {
        const string body = " 原文 😀 \n"; var error = new RerankUpstreamException("failed", 429, body);
        Assert.Equal(429, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(Encoding.UTF8.GetBytes(body), error.Body.ToArray()); Assert.Null(error.ContentType);
    }
    /// <summary>字节入口复制缓冲区；二进制正文不会被有损替换为文本。</summary>
    [Fact]
    public void BinaryErrorKeepsIndependentBytesAndContentType()
    {
        byte[] bytes = [255, 0, 254]; var error = new RerankUpstreamException("failed", 502, bytes, "application/octet-stream"); bytes[0] = 0;
        Assert.Equal(new byte[] { 255, 0, 254 }, error.Body.ToArray()); Assert.Equal(502, error.StatusCode); Assert.Equal("application/octet-stream", error.ContentType);
        Assert.Throws<DecoderFallbackException>(() => error.ResponseBody);
    }
    /// <summary>有效 UTF-8 的字节错误仍可通过既有文本属性读取。</summary>
    [Fact]
    public void Utf8ErrorKeepsExistingTextProjection()
    {
        const string body = " 原生错误 😀 \n"; var error = new RerankUpstreamException("failed", 200, Encoding.UTF8.GetBytes(body), "application/json; charset=utf-8");
        Assert.Equal(body, error.ResponseBody); Assert.Equal(200, error.StatusCode); Assert.Equal("application/json; charset=utf-8", error.ContentType);
    }
}
