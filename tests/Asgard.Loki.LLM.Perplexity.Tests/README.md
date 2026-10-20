# Perplexity offline protocol tests

Run locally with `dotnet test --project tests/Asgard.Loki.LLM.Perplexity.Tests/Asgard.Loki.LLM.Perplexity.Tests.csproj -c Release`.

The independent fixture predates the implementation and contains caller/wire/full result/native event JSON plus literal SSE. Tests invoke the public SDK and supplier adapter with substituted HTTP. They check full data preservation, reverse function results and thought signatures, native response_format/reasoning, sparse failed and partial completed outcomes, exact sequence/response identity, unknown business metadata, strict media/UTF-8/length, pre-dispatch selection, resource ownership, late faults/cancellation and Session affinity. Cache tests use MemoryDistributedCache and an explicit test coordinator.

These tests do not contact a supplier or use credentials, network or Redis. They establish offline protocol behavior; real model availability, native optional-setting acceptance and effect are unverified.
