using System.Net;
using System.Text;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>LLM 原生错误字节扩展保持旧文本入口，不需要任何网络或凭据。</summary>
public sealed class LlmUpstreamErrorTests
{
    /// <summary>既有构造入口、实际状态和原始文本保持；未知媒体类型不伪造。</summary>
    [Fact]
    public void ExistingTextContractIsPreserved()
    {
        const string body = " 原始文本 😀 \n"; var error = new LlmUpstreamException("failed", HttpStatusCode.TooManyRequests, body);
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(Encoding.UTF8.GetBytes(body), error.Body.ToArray()); Assert.Null(error.ContentType);
    }
    /// <summary>原生字节入口复制缓冲区，实际 HTTP200 业务错误也不改成其他状态。</summary>
    [Theory] [InlineData(200)] [InlineData(429)]
    public void Utf8ByteErrorRetainsIndependentNativeData(int status)
    {
        const string body = " 原生错误 😀 \n"; var bytes = Encoding.UTF8.GetBytes(body); var error = new LlmUpstreamException("failed", (HttpStatusCode)status, bytes, "application/json; charset=utf-8"); bytes[0] = 0;
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(body, error.ResponseBody); Assert.Equal(Encoding.UTF8.GetBytes(body), error.Body.ToArray()); Assert.Equal("application/json; charset=utf-8", error.ContentType);
    }
    /// <summary>无效 UTF-8 错误不被替换为文本，原始媒体类型即使不可解析也保留。</summary>
    [Theory] [InlineData("application/octet-stream")] [InlineData("not a media type")]
    public void BinaryErrorsDoNotUseLossyTextProjection(string type)
    {
        byte[] bytes = [255, 0, 254]; var error = new LlmUpstreamException("failed", HttpStatusCode.BadGateway, bytes, type); bytes[0] = 0;
        Assert.Equal(new byte[] { 255, 0, 254 }, error.Body.ToArray()); Assert.Equal(type, error.ContentType); Assert.Throws<DecoderFallbackException>(() => error.ResponseBody);
    }
    /// <summary>没有正文或类型时保持实际缺省，不补造 JSON 或错误字段。</summary>
    [Fact]
    public void EmptyNativeErrorStaysEmpty()
    {
        var error = new LlmUpstreamException("failed", HttpStatusCode.ServiceUnavailable, ReadOnlyMemory<byte>.Empty, null);
        Assert.Empty(error.Body.ToArray()); Assert.Equal("", error.ResponseBody); Assert.Null(error.ContentType);
    }
}
