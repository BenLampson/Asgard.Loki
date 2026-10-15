# Asgard.Loki.Audio

Independent speech capability for Asgard.Loki.

Provides `AddAudio()`, `.Audio.Speech.GenerateAsync()` and `.StreamAsync()`. Public speech routes select explicitly configured adapter targets. Audio aggregation preserves byte order, subtitles, usage and unknown provider metadata, and requires an explicit completion marker.

Register a provider separately, currently `Asgard.Loki.Audio.Volcengine`. This package does not depend on LLM, images, the entry implementation or a provider. It does not reuse LLM Session or routing state.

Speech recognition, music generation and bidirectional speech are not implemented. See the root README for initialization.
