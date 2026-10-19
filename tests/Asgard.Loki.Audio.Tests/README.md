# Asgard.Loki.Audio.Tests

Independent speech routing, ordered aggregation and completion-state tests.

Thirty-eight offline core cases include 35 native/terminal regressions. Application-owned in-memory adapters exercise raw voice objects/JSON, independent snapshots, preserved byte/frame order, whole-route native support, no filtering/retry/failover, borrowed target ownership and source-compatible default methods on legacy clients. Common and native calls both defer the completed chunk until source enumeration/disposal succeeds; tests cover late/duplicate/missing terminals, tail/disposal errors, cancellation and early consumer disposal.

This optional contract checkpoint does not implement a bundled native speech provider, HTTP protocol or media decoder. No mock success here is described as a real binary/SSE or model verification.

xUnit v3 / Microsoft.Testing.Platform. Tests use no real provider credentials or external services.
