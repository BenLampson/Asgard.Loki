using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Asgard.Loki.Audio.Tests;

/// <summary>只实现既有两个成员，验证可选原生方法不破坏外部客户端源码兼容性。</summary>
internal sealed class LegacySpeechClient : ISpeechClient
{
    /// <inheritdoc />
    public Task<SpeechResult> GenerateAsync(SpeechRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new SpeechResult(new byte[] { 1 }, "mp3", []));
    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); yield return new(new byte[] { 1 }, true, JsonSerializer.SerializeToElement(new { }), "legacy"); }
}
