# Asgard.Loki.Rerank

Independent reranking module for the Asgard.Loki .NET SDK. This capability references only `Asgard.Loki.Abstractions`; it does not reference Embedding, LLM or any supplier. It exposes `AddRerank(...)` and `loki.Rerank.RerankAsync(...)` through the existing module mechanism.

## Current support

| Area | Contract |
| --- | --- |
| Registration | Eager configuration validation and collection snapshots; one lazy module per client |
| Complete targets | Each borrowed `IRerankAdapter` owns its server, actual model, credentials and native dialect |
| Routing | Exact public model names; independent round-robin state per route and per client |
| Requests | Public `Model` plus unchanged native JSON `Parameters`; root `model` is reserved |
| Results | Independent complete native `Data` plus public/upstream/provider metadata |
| Failure/cancellation | Propagated from the chosen adapter; no retry or target failover |
| Bundled suppliers | Not included in this core-contract checkpoint; Cohere and Jina follow separately |

```csharp
using Asgard.Loki;
using Asgard.Loki.Rerank;
using System.Text.Json;

IRerankAdapter target = applicationOwnedNativeRerankAdapter;
await using var loki = new LokiClientBuilder().AddRerank(new RerankOptions
{
    Routes = [new RerankRoute { Model = "ranker", Targets = [target] }]
}).Build();

var result = await loki.Rerank.RerankAsync(new RerankRequest
{
    Model = "ranker",
    Parameters = new()
    {
        ["query"] = JsonSerializer.SerializeToElement("search query"),
        ["documents"] = JsonSerializer.SerializeToElement(new[] { "first", "second" }),
        ["top_n"] = JsonSerializer.SerializeToElement(1)
    }
}, cancellationToken);
// Data has your adapter's native shape; it is not a shared normalized ranking model.
```

Query/document input types, top-n semantics, original document indices, score ranges, returned documents and usage remain supplier-specific. Each adapter must validate its native successful/error response and association with the original request. Provider-owned convenience views follow in the corresponding provider package. Core JSON retention alone does not certify that an object is a valid Cohere/Jina ranking response.

Core never formats structured documents as text/YAML, truncates content, changes a query, computes or normalizes scores, sorts results, fabricates indices, fills missing scores or infers token usage. Zero/null/false and unknown nested fields stay in raw JSON. `RerankUpstreamException` is available to preserve native HTTP status/body; ordinary adapter errors also propagate unchanged. There is no automatic supplier conversion or async job hosting.

Configuration collections and each request dictionary/JSON value are copied. Do not mutate a dictionary while its snapshot is being taken; later changes cannot alter the dispatched copy. A pre-cancelled call does not select a target. Modules do not own borrowed adapters/factories or any other capability's resources. No conversational Session, cache, vector database, retrieval engine, RAG pipeline, background service, HTTP listener or billing logic is introduced.

Thirty-four offline tests use custom in-memory adapters through the public SDK. They cover configuration/native JSON snapshots, exact/per-route/per-client/concurrent selection, cancellation, error propagation, model override rejection and borrowed ownership. They use no live Key, HTTP server, Redis, real ranking model or relevance-quality evaluation. Native supplier protocols and live acceptance are separate checkpoints.
