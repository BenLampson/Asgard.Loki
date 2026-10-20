# Asgard.Loki.Rerank.Cohere

Native Cohere reranking for the Asgard.Loki .NET SDK. This supplier references only `Asgard.Loki.Rerank` and standard HTTP dependencies. It has no LLM, Embedding, other supplier, proxy server or RAG pipeline dependency.

## Support matrix

| Operation | Inputs / native settings | Result |
| --- | --- | --- |
| Explicit `V1`, `POST v1/rerank` | Query; nonempty string/document array; document objects require string `text`; `top_n`, `rank_fields`, `return_documents`, `max_chunks_per_doc` | Full ordered native results, original indices/scores, returned documents and metadata |
| Explicit `V2`, `POST v2/rerank` | Query; nonempty string document array; `top_n`, `max_tokens_per_doc`, `priority` | Full ordered native results, original indices/scores and metadata |
| Native extensions | Unknown root/nested JSON fields retained; other-version extensions do not select a version or trigger migration | Full raw `Data`, including unknown/null/false/zero fields |
| HTTP failures | Native status and unmodified response body | `RerankUpstreamException`; one attempt, no retry or version fallback |
| Streaming | Not a native operation for these endpoints | Successful SSE rejected at headers before reading the body |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Rerank;
using Asgard.Loki.Rerank.Cohere;
using System.Text.Json;

var adapter = new CohereRerankAdapter(httpClientFactory, new()
{
    Model = "your-authorized-rerank-model",
    ApiVersion = CohereRerankApiVersion.V2,
    ApiKey = apiKey,
    BaseAddress = new Uri("https://api.cohere.com/")
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
        ["top_n"] = JsonSerializer.SerializeToElement(1)
    }
}, cancellationToken);
var ranking = result.CohereRankings[0];
var originalDocumentIndex = ranking.Index; // Not the position in the returned array.
var nativeItem = ranking.Data;
```

The borrowed factory creates a request-local client. `HttpClientName` selects the caller's named client; the optional `ClientName` sets `X-Client-Name`. `BaseAddress` is the service root, optionally with a tenant/proxy path; the selected `v1/rerank` or `v2/rerank` path is appended. Keys are runtime-validated and excluded from options JSON. Adapters and factories remain caller-owned. No LLM Session or cache is required.

## Fidelity and limitations

The adapter replaces only the public root model with the configured upstream model. It snapshots parameters/documents before awaiting HTTP. It does not inject defaults, clear unsupported fields, trim/normalize strings, convert objects into YAML, sort results, normalize scores, infer usage, split requests or fetch content. Known integer options must be exact integers; the raw decimal/exponent representation is retained. Unknown fields are forwarded, which does not establish acceptance by an upstream model/version.

Results must contain the complete requested number of rankings (`min(top_n, document count)`, or all documents if omitted), unique in-range original indices, and finite Cohere scores in `[0,1]`. Omitted/invalid scores are errors, not zero. In v1, a returned document must have text and every original object field exactly matching its indexed original input; explicit `return_documents=true` requires it to be present. The SDK retains returned document extension fields, and rejects missing/changed original document fields rather than inventing them. No sorting or text normalization repairs an invalid association.

`CohereRankings` and `CohereRerankItem` are provider-owned convenience views; `Data` remains the authoritative raw JSON. The `double` score view has ordinary floating-point precision, whereas the original JSON number remains unchanged. A view constructed without an original request can validate item shape/unique indices, but cannot certify request count or original-document association. Raw usage, warnings and `billed_units` are metadata only; this package does not implement billing or score-quality evaluation.

These are native APIs, not a v1↔v2 converter. Configure a separate complete target for each API version; v2 rejects object documents rather than implicitly serializing them. Token/document limits, ranking quality, chunking and server-side truncation remain upstream behavior; there is no tokenizer, model catalog or automatic parameter correction.

## Validation

The 120 offline xUnit v3 cases use only in-memory HTTP handlers, fragmented UTF-8 responses and custom factories through the public SDK. Four independently authored full-wire fixtures cover v1 text/object and v2 omitted/explicit settings. Negative/lifecycle cases cover request validation, original-index/document association, top-n counts, exact numbers, native errors, malformed/truncated/duplicate JSON, SSE refusal, cancellation, snapshots and borrowed ownership. No real Cohere Key, provider network, Redis, live relevance score or model-quality claim is involved. GitHub CI is reserved for PR/release gates; this implementation is verified locally.

Native contract references: [Cohere v1 Rerank](https://docs.cohere.com/v1/reference/rerank), [Cohere v2 Rerank](https://docs.cohere.com/reference/rerank), [Cohere Rerank best practices](https://docs.cohere.com/docs/reranking-best-practices).
