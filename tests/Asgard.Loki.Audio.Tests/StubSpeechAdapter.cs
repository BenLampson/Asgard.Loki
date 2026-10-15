using System.Runtime.CompilerServices;

namespace Asgard.Loki.Audio.Tests;

/// <summary>为音频模块测试提供确定性分块。</summary>
internal sealed class StubSpeechAdapter(IReadOnlyList<SpeechChunk> chunks) : ISpeechAdapter
{
    /// <inheritdoc />
    public string Id => "stub";
    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechChunk> StreamAsync(SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }
}
