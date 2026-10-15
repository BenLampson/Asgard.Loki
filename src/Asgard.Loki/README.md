# Asgard.Loki

The unified .NET 10 / C# 14 client implementation: `LokiClientBuilder`, lazy module access and asynchronous resource disposal.

Depends only on `Asgard.Loki.Abstractions`. Install capability/provider packages separately to obtain extensions such as `AddLLM()`, `AddAudio()`, `AddImage()`, `.LLM`, `.Audio` and `.Image`.

The client owns modules created by its factories. Cache connections, HTTP factories and other explicitly borrowed dependencies remain caller-owned.
