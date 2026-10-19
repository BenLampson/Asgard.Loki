# Asgard.Loki.LLM.Tests

LLM routing, protocol preservation and Session ownership tests using MemoryDistributedCache and a test coordinator.

Twenty-five explicit credential-kind regressions bring this core suite to 90 cases. They cover nonempty Key compatibility, explicit anonymous identity with no readable Key, eager per-target support validation, complete/stream Session restoration, both authentication-kind changes, target/identity/implementation/version changes, independent configuration snapshots, a real legacy-fingerprint cached Session and concurrent first binding with a shared explicit coordinator. The application-owned fake adapter performs no HTTP or real authentication.

Optional Responses WebSocket tests use an in-memory duplex adapter, without opening sockets. They cover complete-target selection and concurrent first binding, missing/replaced Sessions during handshake/send/read, cross-session continuation/cancellation, native JSON snapshots, multiplexing fields, serialized writes, exclusive readers, cancellation, lifecycle response-ID validation, explicit close/dispose and cache/handshake errors. Bundled-provider transport and framing tests belong to their provider projects; this suite does not claim live WebSocket verification.

xUnit v3 / Microsoft.Testing.Platform. Tests use no real provider credentials or external services.
