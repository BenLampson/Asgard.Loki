# Asgard.Loki.Rerank.SGLang

Native SGLang v0.5.19 `POST rerank` support for the .NET SDK. The package references only Rerank, capability-independent Infrastructure and standard HTTP support. It does not start a model server or reference LLM, Embedding, Image, Audio, Video or other supplier packages.

## Support matrix

| Area | Contract |
| --- | --- |
| Operation | One complete JSON POST under an explicit API root, typically ending in `/v1/` |
| Inputs | Original text or native text/image_url/video_url content-part arrays for query and documents |
| Options | Native instruct, nullable positive top_n, return_documents and unknown extension JSON; no injected defaults |
| Result | Original root array with score/index/document/meta_info and unknown fields; no normalized envelope |
| Association | Exact unique input indices, complete min(top_n, document count) results and matching text echoes |
| Authentication | Explicit Anonymous or fixed BearerToken mode, independent target/named HTTP client |
| Local bounds | Positive MaxRequestBytes (32 MiB), MaxResponseBytes (64 MiB including error bodies) |
| Errors/lifetime | Original status/raw bytes/content type, native errors in HTTP 200, EOF/disposal/final cancellation before success; no retry/failover |
| Offline/live | 88 substitute-HTTP cases with independently authored protocol fixtures; no live server/model/relevance verification |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Rerank;
using Asgard.Loki.Rerank.SGLang;
using System.Text.Json;

// factory, apiRoot, configuredServedModel and cancellationToken are caller-owned.
var target = new SgLangRerankAdapter(factory, new SgLangRerankOptions
{
    Id = "sglang-primary",
    BaseAddress = apiRoot,
    Model = configuredServedModel,
    Authentication = SgLangRerankAuthentication.Anonymous
    // For BearerToken, also set ApiKey to this target's explicit token.
});
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
        ["top_n"] = JsonSerializer.SerializeToElement(1),
        ["return_documents"] = JsonSerializer.SerializeToElement(false)
    }
}, cancellationToken);
foreach (var item in result.SgLangRankings)
    Console.WriteLine($"Input {item.Index}: {item.Score}");
```

The caller supplies the entire API root and served-model identity. The native request schema has no model selector: neither public route Model nor configured target Model is injected into the HTTP body. Metadata records these identities without verifying the actual server deployment. The adapter appends only `rerank`, preserving proxy/version prefixes with or without a trailing slash. Roots must be absolute HTTP(S) URIs without credentials/query/fragment. Default client name is `Asgard.Loki.Rerank.SGLang`. Credentials are excluded from options JSON. Anonymous mode rejects an accompanying token; both modes remove unrelated factory authentication/organization/project defaults.

## Fidelity and limits

Whitespace, document order, native content-part boundaries, null/false, exact JSON numbers and unknown fields remain unchanged. Query/documents must be nonempty native text or content-part arrays. The SDK does not tokenize, truncate, split, join, render prompts/templates or download media. Actual multimodal support, server mode/template/model configuration and handling of unknown options belong to the server. For multimodal documents, use explicit return_documents=false: the pinned native response schema only defines string echoes. Any returned echo that cannot match its original indexed text fails rather than being fabricated or assumed equivalent.

Absent top_n returns all input documents; explicit null remains null. A positive top_n larger than input count caps the expected output count without changing the native parameter or allocating by its value. return_documents defaults to true on the server; this is validated without inserting the field. Missing/null echoes fail when required; present string echoes must equal the indexed original text. Empty, partial, duplicate or unrelated indices fail. An explicit stream request is unsupported and SSE is rejected before reading a successful stream body.

Scores are finite native numbers, with no assumed zero-to-one range, normalization, sorting, recomputation or probability guarantee. Original array order remains authoritative. The provider-owned SgLangRankings view exposes native Index, a double Score convenience projection, optional Document/MetaInfo and a complete independent Data snapshot; raw score precision stays in Data. Absent metadata remains absent, JSON null stays distinguishable, and no usage is invented or summed from per-item metadata. Jina results/relevance_score envelopes are different protocols and fail explicitly.

HTTP failures retain actual StatusCode, copied Body bytes and ContentType through RerankUpstreamException. Native object:error and error-wrapper JSON in HTTP 200 also fail with their actual bytes/status. The existing ResponseBody property returns strict UTF-8 for byte errors; invalid binary errors must be read through Body. Successful JSON requires valid UTF-8 and unique members, including unknown nested strings/fields; supplied content type must be application/json with optional UTF-8 charset. Malformed/truncated JSON, byte-limit/length errors, changed actual target/method, late read/disposal failures or cancellation cannot publish a successful result.

IHttpClientFactory and adapters remain borrowed. Per-call client/request/response/stream resources are disposed before the final cancellation check and result publication. Caller-configured transport policies must preserve single-attempt/fixed-target behavior. No server hosting, login, discovery, model catalog, conversion, background polling, vector database, RAG, billing or media management is included. All protocol tests are offline; they do not certify live acceptance, ranking relevance or multimodal interpretation.

Contract references: SGLang v0.5.19 [request/result schema](https://github.com/sgl-project/sglang/blob/v0.5.19/python/sglang/srt/entrypoints/openai/protocol.py) and [native rerank implementation](https://github.com/sgl-project/sglang/blob/v0.5.19/python/sglang/srt/entrypoints/openai/serving_rerank.py).
