using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>聚合完成的音频内容及全部帧元数据。</summary>
public sealed record SpeechResult(ReadOnlyMemory<byte> Audio, string Format, IReadOnlyList<JsonElement> Frames);
