# Asgard.Loki.Video.GoogleVertex.Tests

Offline xUnit v3 tests for resource-scoped Vertex Veo submission and explicit `fetchPredictOperation` queries. Fixtures use fictional prompts, resources, GCS paths, media and tokens. They are not live model output.

Native examples follow the official [text](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/video/generate-videos-from-text) and [image](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/video/generate-videos-from-an-image) REST guides. Fixtures retain `instances`, `parameters`, original full operation names, omitted `done`, errors, filtering, multiple native `videos`, GCS references, inline bytes and unknown fields. Local fidelity tests do not establish model/account availability or generated-video validity.
