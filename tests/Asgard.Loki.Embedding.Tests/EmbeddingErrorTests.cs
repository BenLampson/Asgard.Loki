using System.Text;
using Xunit;

namespace Asgard.Loki.Embedding.Tests;

/// <summary>嵌入错误的新旧公开构造契约，原始字节不能被解码替换或借用后改写。</summary>
public sealed class EmbeddingErrorTests
{
    /// <summary>保留命名参数、状态和原始文本，旧入口不伪造 Content-Type。</summary>
    [Fact]
    public void LegacyConstructorPreservesOriginalTextContract()
    {
        const string original = "原始错误 😀\nplain text";
        var error = new EmbeddingUpstreamException(message: "failure", statusCode: 429, responseBody: original);
        Assert.Equal("failure", error.Message); Assert.Equal(429, error.StatusCode); Assert.Equal(original, error.ResponseBody);
        Assert.Equal(Encoding.UTF8.GetBytes(original), error.Body.ToArray()); Assert.Null(error.ContentType);
    }
    /// <summary>字节入口独立复制源缓冲区，保留实际 HTTP 状态及原始 charset 信息。</summary>
    [Fact]
    public void ByteConstructorCopiesBodyAndKeepsActualMetadata()
    {
        var source = Encoding.UTF8.GetBytes("原生完整错误"); var expected = source.ToArray();
        var error = new EmbeddingUpstreamException("native error", 200, source, "application/problem+json; charset=\"UTF-8\"");
        source.AsSpan().Clear(); Assert.Equal(expected, error.Body.ToArray()); Assert.Equal(200, error.StatusCode);
        Assert.Equal("原生完整错误", error.ResponseBody); Assert.Equal("application/problem+json; charset=\"UTF-8\"", error.ContentType);
    }
    /// <summary>二进制错误体原样可用；请求文本时明确失败，不返回有损替代字符。</summary>
    [Fact]
    public void BinaryBodyIsNeverReplacedByLossyUtf8()
    {
        byte[] bytes = [255, 0, 254]; var error = new EmbeddingUpstreamException("binary", 502, bytes, "application/octet-stream");
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(502, error.StatusCode);
        Assert.Throws<DecoderFallbackException>(() => error.ResponseBody);
    }
}
