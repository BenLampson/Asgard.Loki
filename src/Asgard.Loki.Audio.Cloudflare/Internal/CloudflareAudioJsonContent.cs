using System.Net;
using System.Security.Cryptography;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.Audio.Cloudflare.Internal;

/// <summary>供应商原生 Base64 JSON 流式编码，不预缓冲、回卷或关闭调用者上传。</summary>
internal sealed class CloudflareAudioJsonContent(Stream source, byte[] parameters, long maximumBytes) : HttpContent
{
    private readonly BorrowedStreamContent _input = new(source, maximumBytes);
    private int _serialized;
    /// <summary>未知流长度不触发预读。</summary>
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    /// <inheritdoc />
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);
    /// <summary>只读取一次原始字节，并保持所有参数的原生 JSON 类型。</summary>
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _serialized, 1) != 0) throw new InvalidOperationException("Cloudflare 音频上传不能隐式重放。");
        await stream.WriteAsync(parameters.AsMemory(0, parameters.Length - 1), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(",\"audio\":\""u8.ToArray(), cancellationToken).ConfigureAwait(false);
        using var transform = new ToBase64Transform();
        await using (var encoded = new CryptoStream(stream, transform, CryptoStreamMode.Write, leaveOpen: true))
        {
            await _input.CopyToAsync(encoded, cancellationToken).ConfigureAwait(false);
            await encoded.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
        }
        await stream.WriteAsync("\"}"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>只释放调用内内容对象，原始上传继续由调用者拥有。</summary>
    protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
}
