# Asgard.Loki.Audio.Tests

Independent Audio routing, raw file operations, ordered speech aggregation and completion-state tests.

Thirty-eight offline core cases include 35 native/terminal regressions. Application-owned in-memory adapters exercise raw voice objects/JSON, independent snapshots, preserved byte/frame order, whole-route native support, no filtering/retry/failover, borrowed target ownership and source-compatible default methods on legacy clients. Common and native calls both defer the completed chunk until source enumeration/disposal succeeds; tests cover late/duplicate/missing terminals, tail/disposal errors, cancellation and early consumer disposal.

The file-operation extension adds 84 cases, bringing the core total to 122. Application-owned adapters cover isolated transcription/translation registration, literal ordered/repeated multipart text fields, immutable metadata snapshots and caller-owned nonseekable streams. Complete authored `transcription-core.json` and `translation-core.txt` preserve native segments/words/speakers/usage/unknown JSON and WEBVTT bytes. Event tests preserve complete native JSON/SSE identity and hold the original terminal until enumeration/disposal succeeds. Mixed streaming targets, missing/late/duplicate events, real DisposeAsync failure, cancellation, early stop, no retry/failover and borrowed resources are checked.

These fixtures are core contract examples, not HTTP multipart wire snapshots or supplier acceptance tests. No core mock success is described as actual OpenAI transcription/translation, media decoding or model verification. The existing separate OpenAI Speech provider test project owns synthesis binary/SSE wire coverage.

xUnit v3 / Microsoft.Testing.Platform. Tests use no real provider credentials or external services.
