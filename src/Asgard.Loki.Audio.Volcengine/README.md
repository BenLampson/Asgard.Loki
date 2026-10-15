# Asgard.Loki.Audio.Volcengine

Volcengine V3 HTTP chunked text-to-speech adapter for Asgard.Loki.

## Supported

- `POST /api/v3/tts/unidirectional` with ordered audio chunks and full-result aggregation.
- New `X-Api-Key` authentication or legacy `X-Api-App-Id` + `X-Api-Access-Key`, configured exclusively.
- Explicit resource ID, voice, format, sample rate and unmodified extension parameters.
- Base64 decoding per frame; full subtitle, usage and unknown metadata retention.
- Business-code errors and missing completion frames fail explicitly; no automatic generation retry.

Create `VolcengineSpeechAdapter(IHttpClientFactory, VolcengineSpeechOptions)` and include it in an `AudioOptions.SpeechRoutes` target list. The default named HTTP client is `Asgard.Loki.Audio.Volcengine`. The default endpoint is `https://openspeech.bytedance.com/api/v3/tts/unidirectional`.

Ark LLM credentials are not assumed to authorize speech. Resource and voice permissions require separate verification. No ASR, music generation, WebSocket speech or replay is implemented.

Tests use local HTTP stubs. See the root README for initialization and `docs/architecture.md` for module boundaries.
