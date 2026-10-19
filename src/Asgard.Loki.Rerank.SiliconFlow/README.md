# Asgard.Loki.Rerank.SiliconFlow

Native SiliconFlow reranking for the Asgard.Loki .NET SDK. This package references only `Asgard.Loki.Rerank` and standard HTTP dependencies. It contains no LLM Session, media downloader, retriever, RAG workflow, server or billing implementation.

## Support matrix

| Area | Native contract |
| --- | --- |
| Operation | One complete `POST rerank` call relative to the configured versioned API root |
| Query | Nonempty text string or native ImageDoc object |
| Documents | Original scalar text string or nonempty array of strings/TextDoc/ImageDoc objects |
| Settings | Explicit `top_n`, `return_documents`, `instruction`, `max_chunks_per_doc`, `overlap_tokens`; no injected defaults |
| Results | Native `id/results`, original indices and finite scores, optional returned documents and complete extension JSON |
| Metadata | Current optional `meta.tokens` and older published `tokens` containers retained independently; missing counts stay unknown |
| Errors | Original HTTP status/UTF-8 body and known native business errors, with one attempt |
| Streaming | Not implemented by this endpoint; non-JSON success types, including SSE/NDJSON, fail before body reads |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Rerank;
using Asgard.Loki.Rerank.SiliconFlow;
using System.Text.Json;

var adapter = new SiliconFlowRerankAdapter(httpClientFactory, new()
{
    Model = "your-authorized-rerank-model",
    ApiKey = apiKey,
    BaseAddress = new Uri("https://api.siliconflow.cn/v1/")
});
await using var loki = new LokiClientBuilder().AddRerank(new()
{
    Routes = [new() { Model = "ranker", Targets = [adapter] }]
}).Build();

var result = await loki.Rerank.RerankAsync(new()
{
    Model = "ranker",
    Parameters = new()
    {
        ["query"] = JsonSerializer.SerializeToElement("search query"),
        ["documents"] = JsonSerializer.SerializeToElement(new[] { "first", "second" }),
        ["top_n"] = JsonSerializer.SerializeToElement(1),
        ["return_documents"] = JsonSerializer.SerializeToElement(true)
    }
}, cancellationToken);
var originalDocumentIndex = result.SiliconFlowRankings[0].Index;
var nativeItem = result.SiliconFlowRankings[0].Data;
```

`BaseAddress` includes the service version and optional proxy prefix; the adapter appends only `rerank`. `HttpClientName` selects a borrowed named factory client. API Keys are excluded from options JSON and appear only in the authorization header. Caller-owned handlers can supply tracing headers. The SDK releases its request/response/client, never the borrowed factory/handler or adapter. Configure caller-owned HTTP handlers without automatic retries/hedging when retaining the single-attempt contract.

## Fidelity and limitations

The request is snapshotted before awaiting HTTP. Only root model binding changes: native scalar documents remain scalar, arrays/objects keep order, text is not trimmed, metadata/null/false fields remain native, and no MIME or model-name heuristics are applied. Query objects accept the published image form; a text object is not a substitute for a query string. Ambiguous text/image/video objects fail. Text documents have no locally invented minimum length; empty native strings remain distinct from a missing document list. Invalid required containers, option types, exact integer constraints and the native overlap range fail before HTTP. Unknown extensions are forwarded without promising upstream acceptance.

Returned rankings must contain exactly `min(top_n, input count)` entries, or all candidates when top_n is omitted, with unique in-range original indices. Explicit `return_documents=true` requires each document. Every returned document is checked against its indexed original content even when optional. Text wrappers can retain the original textual value/reference; an explicit image kind cannot contradict a text source. Unknown input metadata has no invented echo guarantee. Repeated document values do not collapse distinct indices, and native result order is never sorted by the SDK.

Scores require finite numbers without assuming another supplier's score interval. `RelevanceScore` is a double convenience view; authoritative raw JSON numbers remain in `Data`. Exact token counters and integer settings are not capped at Int32 merely to fit the SDK; only native indices and bounded overlap need local array/range integers. Unknown/missing counters are not synthesized, and raw `meta.billed_units` is passive upstream JSON. It does not authorize or implement billing logic.

Responses require valid complete UTF-8 JSON without duplicate properties. Known error/code responses cannot become success by including results. Body-read, response/client-disposal failures and cancellation through final disposal suppress successful return. Standalone `SiliconFlowRankings`/`SiliconFlowRerankItem` views own snapshots and validate native shape; without the original request they cannot prove input identity, count or index range.

This is a separate supplier because Cohere's array/version/document rules and Jina's required model/usage envelope do not establish the full SiliconFlow contract. No dependency on either provider is added. Model-specific image/options acceptance, actual chunking and ranking quality remain upstream behavior. Although the current documentation intro mentions video, its published concrete request schema exposes text/image forms only; video object shapes are not claimed or fabricated here. Additional formats and live account behavior require separate evidence.

## Validation

143 offline xUnit v3 cases cover six independently authored full request/response fixtures through the public SDK, fragmented Unicode responses, scalar/empty/array/media containers, exact numbers, original document indices, optional/required returns and metadata preservation. Negative/lifecycle cases cover options, errors, malformed/duplicate/truncated JSON, unsupported stream media, invalid UTF-8, cancellation at send/body/EOF/disposal, request snapshots and borrowed ownership. No real Key, provider call, image download, relevance-quality evaluation or remote CI run was used.

Contracts inspected on 2026-10-19: [current SiliconFlow rerank reference](https://docs.siliconflow.cn/docs/api/rerank-post), including its embedded Classic/VL schema, and the [older published rerank reference](https://siliconflow.readme.io/reference/creatererank) for the legacy token envelope. These are offline protocol checks; supported model/account behavior is not certified.
