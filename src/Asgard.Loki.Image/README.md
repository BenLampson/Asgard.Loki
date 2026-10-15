# Asgard.Loki.Image

Independent image generation contracts, routing and module registration for Asgard.Loki.

Provides `AddImage()`, `.Image.GenerateAsync()` and `.Image.StreamAsync()`. Requests retain arbitrary JSON parameters; results preserve full provider JSON, image data, URLs, usage and metadata. `GeneratedImage.Decode()` decodes Base64 on demand without downloading URL results.

Install a provider separately, currently `Asgard.Loki.Image.OpenAI`. This package depends only on the generic abstractions and does not reference LLM or audio.

Image edits and variations are not implemented. See the root README for initialization.
