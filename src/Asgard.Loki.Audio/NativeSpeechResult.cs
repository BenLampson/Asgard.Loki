using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>聚合完整原生语音字节及供应商帧，不猜测供应商缺省输出格式。</summary>
public sealed record NativeSpeechResult(ReadOnlyMemory<byte> Audio, IReadOnlyList<JsonElement> Frames);
