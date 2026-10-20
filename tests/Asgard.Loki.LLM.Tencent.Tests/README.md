# Tencent Hunyuan offline tests

This xUnit v3 project currently executes 86 offline cases with substitute HTTP, a fixed logical clock, an application-owned credential-source double and `MemoryDistributedCache` plus an explicit test-only Session coordinator. No actual Key, Tencent/STS/model call, Redis or external network is used.

`Fixtures/hunyuan.json` and its separate SSE window were authored before production code and retain complete native request/Response/error/event envelopes. `Fixtures/tc3.json` contains eight independent Python standard-library HMAC vectors. The four complete JSON vectors are exercised through the public provider and actual captured HTTP bytes/headers; the four stream vectors and SSE fixture are not yet exercised by a native transport and do not establish SSE support. Canonical URI stays `/`, even with a physical proxy prefix or port.

Tests compare entire native JSON, tool/schema strings and IDs, unknown fields and exact numbers. They check stable-principal Key rotation, source/principal/expiry/cancellation boundaries, request snapshots before credential waits, whole-Session provider configuration drift, method/target/body/header mutations, borrowed source/factory ownership, fragmented nonseekable reads, EOF/length/byte/encoding/media/duplicate/disposal failures and raw HTTP/native error preservation. SSE currently has an explicit unsupported/no-side-effects test.

After restoring/building Release locally, run `dotnet test --project tests/Asgard.Loki.LLM.Tencent.Tests/Asgard.Loki.LLM.Tencent.Tests.csproj -c Release --no-build`. These are protocol/signature contract checks, not live authentication, service availability, model quality or native stream validation.
