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
        var bytes = new ArrayBufferWriter<byte>();
        await TransferAsync(content, maximumBytes, (chunk, _) => { bytes.Write(chunk.Span); return ValueTask.CompletedTask; }, cancellationToken).ConfigureAwait(false);
        var result = bytes.WrittenMemory.ToArray(); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <summary>完整复制有界原始正文，释放内容流但借用目标流；失败时目标可能包含已写入的部分字节。</summary>
    /// <param name="content">调用者拥有的 HTTP 内容。</param>
    /// <param name="destination">借用的可写目标流，从当前位置写入，不关闭、回卷、清空或隐式刷新。</param>
    /// <param name="maximumBytes">允许复制的原始字节上限，必须大于零。</param>
    /// <param name="cancellationToken">贯穿读取、写入及最终释放检查的取消令牌。</param>
    /// <returns>完整正文已写入的字节数，外层 HTTP 资源释放仍由调用者完成。</returns>
    public static Task<long> CopyToAsync(HttpContent content, Stream destination, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("HTTP 正文目标流必须可写。", nameof(destination));
        return TransferAsync(content, maximumBytes, destination.WriteAsync, cancellationToken);
    }
    /// <summary>两个公开入口共享字节、EOF 和资源边界，写入策略不包含任何能力协议。</summary>
    private static async Task<long> TransferAsync(HttpContent content, long maximumBytes,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content); ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);
        cancellationToken.ThrowIfCancellationRequested(); var expected = content.Headers.ContentLength;
        if (expected > maximumBytes) throw new InvalidDataException("HTTP 正文的声明长度超过显式字节上限。");
        long total = 0;
        await using (var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (count > maximumBytes - total || expected is { } declared && count > declared - total) throw new InvalidDataException("HTTP 正文超过字节上限或声明长度。");
                    await write(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false); total += count;
                }
                if (expected is { } size && total != size) throw new InvalidDataException("HTTP 正文未达到声明长度。");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        cancellationToken.ThrowIfCancellationRequested(); return total;
    }
}
