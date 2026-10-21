# Asgard.Loki.LLM.Tests

LLM routing, protocol preservation and Session ownership tests using MemoryDistributedCache and a test coordinator.

Twenty-five explicit credential-kind regressions bring this core suite to tests. They cover nonempty Key compatibility, explicit anonymous identity with no readable Key, eager per-target support validation, complete/stream Session restoration, both authentication-kind changes, target/identity/implementation/version changes, independent configuration snapshots, a real legacy-fingerprint cached Session and concurrent first binding with a shared explicit coordinator. The application-owned fake adapter performs no HTTP or real authentication.

Optional Responses WebSocket tests use an in-memory duplex adapter, without opening sockets. They cover complete-target selection and concurrent first binding, missing/replaced Sessions during handshake/send/read, cross-session continuation/cancellation, native JSON snapshots, multiplexing fields, serialized writes, exclusive readers, cancellation, lifecycle response-ID validation, explicit close/dispose and cache/handshake errors. Bundled-provider transport and framing tests belong to their provider projects; this suite does not claim live WebSocket verification.

xUnit v3 / Microsoft.Testing.Platform. Tests use no real provider credentials or external services.

An application-owned adapter verifies complete/streamed native JSON, event names/raw indices, absent/null/zero/unknown values, unchanged inputs and snapshots before Session waits. Invalid parameters/Unicode/registration, unsupported routes and final cancellation fail without invoking the wrong protocol. The new core has no AWS dependency and makes no network calls; provider-specific signing/HTTP fixtures belong to the AWS test project. A base-event regression verifies Converse Type follows the transport event name while unknown payload type fields remain raw and Claude semantics stay unchanged. Numeric flag 64 is additive; previous protocol/fingerprint contracts remain covered by their existing regressions.
Tests use an application-owned native adapter and real memory cache/coordinator, independent of AWS. They check raw complete/stream payload snapshots before Session waits, exact model/metadata, native envelope type versus outer event name, zero/null/unknown fields, invalid input before binding, protocol registration/routing and final cancellation. This validates the SDK abstraction, not AWS binary stream support.

Optional protocol declarations are covered by tests. They distinguish physical interfaces from immutable protocol scope, check all configured/backup targets before invocation, preserve legacy complete/stream calls and reject false claims or invalid flags. Configuration never calls a model or acquires credentials to determine support.

## Native text Completions

Text-protocol and registration regressions are covered separately. The text cases use an in-memory native adapter and `MemoryDistributedCache` with explicit coordination. They verify full-target/credential/implementation affinity and drift rejection, old enum values, eager adapter enforcement, separate Chat/text Sessions, native prompt shapes and absence/null, final actual-model/method stream mapping, immutable original/result/event data, SSE metadata, snapshots before an asynchronous cache read, invalid overrides/Unicode/Undefined before selection, final cancellation even when an adapter ignores it, single-attempt stream failure and text-result exclusion from Responses ownership. These tests prove core orchestration and native data preservation; supplier transport/terminal tests and real-model validation are separate.

## Native Cohere v1 Chat

The core suite has tests, including tests and one added physical-adapter registration case. Independent whole request/response/event resources verify full native history/tools/results/references, exact large integers and optional IDs; MemoryDistributedCache with an explicit test coordinator verifies full-target affinity/drift, protocol isolation, snapshots before the first cache wait, invalid Unicode/overrides before cache access, no retry, borrowed provider ownership and final cancellation. In-memory adapters establish no native HTTP/NDJSON transport or live model acceptance.

## Native Workers AI text core

The core suite executes tests, including tests and one added physical-interface registration case. Complete literal request/envelope/event fixtures prove absence of body model injection, original prompt/messages/tool/settings/unknown data, exact numbers and raw native DONE versus JSON. Native data and SSE identity remain separate. MemoryDistributedCache plus explicit test coordination covers full account/server/model/key/adapter/protocol binding, drift, snapshots before the first async cache read, invalid input before cache access, no retry and borrowed providers. Final cancellation is checked before results/events and after the last frame. These in-memory adapter tests do not establish native Cloudflare HTTP/SSE or live model/service acceptance.
