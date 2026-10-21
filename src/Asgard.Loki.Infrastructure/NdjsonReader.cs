using System.Buffers;
using System.Runtime.CompilerServices;

namespace Asgard.Loki.Infrastructure;

/// <summary>仅按 LF 分割有界原始记录；不解释 JSON、空白或能力终态。</summary>
public static class NdjsonReader
{
    /// <summary>返回独立记录字节，移除 LF 并保留 CR、空白和非法编码；借用输入流且不重试。</summary>
    /// <param name="source">借用的输入流，不读取其长度、位置或关闭它。</param>
    /// <param name="maximumRecordBytes">单条记录上限，不计 LF，计入原始 CR；必须大于零。</param>
    /// <param name="maximumTotalBytes">原始流的总字节上限，包含全部分隔符；必须大于零。</param>
    /// <param name="cancellationToken">贯穿读取、记录交付和最终 EOF 检查的取消令牌。</param>
    /// <param name="expectedLength">可选原始 Content-Length，必须非负且不超过总上限。</param>
    /// <returns>包括空行的原始记录；非空末记录允许没有 LF。正常枚举完成不代表供应商成功。</returns>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(Stream source, int maximumRecordBytes,
        long maximumTotalBytes, [EnumeratorCancellation] CancellationToken cancellationToken = default, long? expectedLength = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumRecordBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumTotalBytes, 0);
        if (expectedLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedLength > maximumTotalBytes) throw new InvalidDataException("NDJSON 声明长度超过原始流字节上限。");
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var record = new ArrayBufferWriter<byte>(Math.Min(maximumRecordBytes, 4096));
        long received = 0;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count > maximumTotalBytes - received || expectedLength is { } length && count > length - received)
                    throw new InvalidDataException("NDJSON 流超过原始字节上限或声明长度。");
                received += count;
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var value = buffer[i];
                    if (value == (byte)'\n')
                    {
                        var detached = record.WrittenMemory.ToArray(); record.Clear();
                        yield return detached; continue;
                    }
                    if (record.WrittenCount >= maximumRecordBytes) throw new InvalidDataException("NDJSON 记录超过字节上限。");
                    record.GetSpan(1)[0] = value; record.Advance(1);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedLength is { } expected && received != expected) throw new InvalidDataException("NDJSON 流未达到声明长度。");
            if (record.WrittenCount != 0) yield return record.WrittenMemory.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
