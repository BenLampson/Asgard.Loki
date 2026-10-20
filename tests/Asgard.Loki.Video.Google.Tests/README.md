# Asgard.Loki.Video.Google.Tests

Offline xUnit v3 tests for native Gemini API Veo submission, operation queries and caller-driven content transfer. Fixtures use fictional prompts, identifiers and media bytes; they are not live model output.

The native REST fixtures follow [predictLongRunning](https://ai.google.dev/api/models#method:-models.predictlongrunning) and the [Veo REST examples](https://ai.google.dev/gemini-api/docs/veo). They retain `instances`, `parameters`, `generatedSamples`, omitted `done`, native errors, filtering and unknown fields. They do not adopt gateway task identifiers or the Python/JavaScript SDK's translated object names.

Google has [announced](https://ai.google.dev/gemini-api/docs/deprecations/) shutdown of the Veo 3.1 preview models on **2026-10-22**. Tests establish local wire behavior only; current provider availability and model acceptance require separate live verification.
