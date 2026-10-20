# Asgard.Loki.Rerank.Ali

Native Alibaba Cloud DashScope rerank provider for the Asgard.Loki .NET SDK. It references only `Asgard.Loki.Rerank` and standard HTTP dependencies. Complete target selection and module registration reuse the existing Rerank capability; LLM, Embedding, Image, Video and other suppliers remain independent.

## Support matrix

| Area | Contract |
| --- | --- |
| Operation | One `POST services/rerank/text-rerank/text-rerank` under an explicitly configured API root |
| Request | Native nested `input.query` / `input.documents` and optional `parameters`; only public model alias is replaced |
| Query | Nonempty text string or a native single `text` / `image` object |
| Documents | Ordered nonempty strings or single `text` / `image` / `video` objects; mixed batches remain unchanged |
| Parameters | Positive integer `top_n`, boolean `return_documents`, string `instruct`, numeric `fps` in `[0,1]`, unknown JSON fields |
| Results | Complete native `request_id`, `output.results`, usage and future fields; original result array order and document indices |
| Views | Provider-owned `result.AliRankings` with `Index`, native `RelevanceScore`, `Document` and complete item `Data` |
| Validation | Result count, unique/in-range source indices, exact score bounds, requested document echo and nonnegative native token counters |
| Authentication | Explicit API Key/Bearer and caller-owned named `IHttpClientFactory`; no environment lookup or workspace inference |
| Failures | Original status/body, 200 business-error detection, malformed/duplicate/truncated JSON and stream rejection; cancellation without retries |

```csharp
using Asgard.Loki;
using Asgard.Loki.Rerank;
using Asgard.Loki.Rerank.Ali;
using System.Text.Json;

var target = new AliRerankAdapter(applicationHttpClientFactory, new()
{
    Id = "workspace-ranking",
    Model = "qwen3-rerank",
    BaseAddress = callerSelectedDashScopeApiRoot, // Explicit workspace/region root including /api/v1/. ApiKey = callerProvidedApiKey
});
await using var loki = new LokiClientBuilder().AddRerank(new()
{
    Routes = [new() { Model = "ranker", Targets = [target] }]
}).Build();

var result = await loki.Rerank.RerankAsync(new RerankRequest
{
    Model = "ranker",
    Parameters = new()
    {
        ["input"] = JsonSerializer.SerializeToElement(new
        {
            query = "relevant topic",
            documents = new[] { "first passage", "second passage", "third passage" }
        }),
        ["parameters"] = JsonSerializer.SerializeToElement(new
        {
            top_n = 2,
            return_documents = true
        })
    }
}, cancellationToken);
var rankings = result.AliRankings; // Use each original Index, not its array position.
// result.Data retains native request_id, output, usage and future fields.
```

## Native contract and limits

`BaseAddress` is required rather than selected from a model name. Supply the API root assigned to your region/workspace, including `/api/v1/`; a tenant prefix and optional trailing slash are retained. The adapter appends only the native operation path, never chooses another region, appends model suffixes or borrows another capability's Key. The API Key is excluded from options serialization. No endpoint/model availability or account permission is inferred from a successfully constructed adapter.

The SDK accepts the native HTTP envelope, not DashScope Python SDK's flattened call arguments or its additional response wrapper. Known flat `query`, `documents`, `top_n`, `return_documents`, `instruct` and `fps` fields are rejected rather than moved or silently ignored. Omitted options remain omitted: the native default returns all documents without their text. Positive `top_n` expects `min(top_n, input count)` results; it is never changed to 1 or injected into a request. Null, zero, negative and fractional top-n values are outside this provider's validated request contract. Explicit nested options must have their documented JSON types. Exact integer/score checks inspect raw decimal semantics, avoiding rounding near zero/one or integer boundaries. Large JSON integers are preserved rather than narrowed; model/server limits still apply upstream.

String and object document containers, Unicode and unknown fields remain unchanged. The SDK does not tokenize, truncate, serialize objects into text/YAML, fetch a URL, render media, change encodings or infer which model supports a modality. Image references/Data URIs and video references are passed as caller-provided strings; formats, accessibility and model-specific acceptance are checked upstream. Unknown fields are forwarded without certifying acceptance.

Each result must retain a unique original index within the input documents. A returned document is checked even when `return_documents` is false. When explicitly requested, it cannot be absent/null. Text echoes are matched exactly to the indexed source content; the documented native `document.text` wrapper can echo that content for a media input, using this request's source index context. An explicitly returned image/video modality must match the source modality and value. Missing and explicit null optional documents remain distinct in the provider view; other native fields remain in `Data`. Such checks establish protocol association, not media interpretation or relevance quality. The SDK never sorts/recalculates returned scores, compares them across requests or treats them as calibrated probabilities.

The required native `request_id`, nested results and `usage.total_tokens` are validated. Optional `usage.prompt_tokens` is validated when present, without a model-name rule or fabricated default. Counters remain exact JSON integers, including values beyond `2^53`; no price, billing total or generic usage model is introduced. A nonempty native `code`/`message` or nonnull `error` is a failure even with HTTP 200 and valid-looking results. Error status/body are retained. SSE/NDJSON, duplicate properties, multiple/truncated JSON values and incomplete result counts fail without retry or target failover. Responses are disposed; the factory, handlers and registered adapters remain borrowed.

There is no retrieval pipeline, vector database, RAG coordinator, background worker, HTTP listener, channel/key management, model catalog or billing service. This provider does not implement Ali Embedding, native LLM or a cross-vendor conversion layer.

## Verification

Six independently authored complete request/response fixtures and 124 offline xUnit v3 cases cover text/top-n, mixed media, image queries, native document echoes, raw fields/precision, counts/indices/source association, invalid/unknown containers, business/HTTP errors, cancellation, request snapshots and borrowed ownership. HTTP is substituted; fragmented response reads are exercised. Tiny Base64 examples and `.invalid` URLs are transport samples, not verified media or external resources. No real DashScope model, regional account, returned relevance or media decoder has been tested.

Contracts checked on 2026-10-19: [official Chinese HTTP reference](https://help.aliyun.com/zh/model-studio/text-rerank-api) and [official international HTTP reference](https://www.alibabacloud.com/help/en/model-studio/text-rerank-api). The dated check records the nested current text/media API; it is not a promise about every deployed model, workspace or historical endpoint.
