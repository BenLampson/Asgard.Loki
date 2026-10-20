using System.Buffers;

namespace Asgard.Loki.Infrastructure;

/// <summary>读取有界 HTTP 正文字节，不解释媒体类型、JSON、供应商错误或能力终态。</summary>
public static class HttpBodyReader
{
    /// <summary>完整读取并释放内容流，验证原始长度和上限；借用的 HttpContent 仍由调用者释放。</summary>
    /// <param name="content">由调用者拥有的 HTTP 内容，读取的流在交付字节前释放。</param>
    /// <param name="maximumBytes">允许读取的原始正文字节上限，必须大于零。</param>
    /// <param name="cancellationToken">贯穿读取和最终释放检查的取消令牌。</param>
    /// <returns>完整正文的独立字节数组；调用者仍需等待外层 HTTP 资源释放后再判定调用成功。</returns>
    public static async Task<byte[]> ReadAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);
        cancellationToken.ThrowIfCancellationRequested(); var expected = content.Headers.ContentLength;
        if (expected > maximumBytes) throw new InvalidDataException("HTTP 正文的声明长度超过显式字节上限。");
        byte[] result;
        await using (var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var bytes = new ArrayBufferWriter<byte>(); var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    var length = (long)bytes.WrittenCount + count;
                    if (length > maximumBytes || expected is { } declared && length > declared) throw new InvalidDataException("HTTP 正文超过字节上限或声明长度。");
                    bytes.Write(buffer.AsSpan(0, count));
                }
                if (expected is { } size && bytes.WrittenCount != size) throw new InvalidDataException("HTTP 正文未达到声明长度。");
                result = bytes.WrittenMemory.ToArray();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }
}
