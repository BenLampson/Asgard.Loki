# Asgard.Loki.Embedding.OpenAI

Native OpenAI Embeddings provider for the Asgard.Loki .NET SDK. It references only `Asgard.Loki.Embedding` and standard HTTP dependencies; it does not install or share LLM/Image providers, Sessions or credentials.

## Support matrix

| Area | Implemented contract |
| --- | --- |
| Operation | One `POST embeddings` under the configured API root |
| Input | Nonempty text, text arrays, one token-ID array, or arrays of token-ID arrays; original order and boundaries retained |
| Parameters | Explicit positive `dimensions`, optional `encoding_format` (`float`/`base64`), `user` and unknown extension JSON |
| Output | Complete native JSON, original `data` order/indices, Float or Base64 embedding values, usage and unknown fields |
| Views | Provider-owned `result.OpenAiVectors`, each with original `Index`, `Data`, `Values` or `Base64`; explicit `DecodeBytes()` |
| Authentication | Fixed API Key/Bearer token, optional organization/project, explicit named HTTP factory |
| Errors/cancellation | Original HTTP status/body, 200 business-error detection, cancellation, no retry or failover |
| Offline/live status | 106 substitute-HTTP tests, including 19 Jina compatibility/boundary cases; no live provider/model/vector-quality verification |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Embedding;
using Asgard.Loki.Embedding.OpenAI;
using System.Text.Json;

// httpClientFactory, apiKey, configuredEmbeddingModel and cancellationToken are caller-owned.
var target = new OpenAiEmbeddingAdapter(httpClientFactory, new OpenAiEmbeddingOptions
{
    Id = "embedding-primary",
    Model = configuredEmbeddingModel,
    ApiKey = apiKey,
    BaseAddress = new Uri("https://api.openai.com/v1/")
});
await using var loki = new LokiClientBuilder().AddEmbedding(new EmbeddingOptions
{
    Routes = [new EmbeddingRoute { Model = "vectors", Targets = [target] }]
}).Build();

var result = await loki.Embedding.EmbedAsync(new EmbeddingRequest
{
    Model = "vectors",
    Parameters = new()
    {
        ["input"] = JsonSerializer.SerializeToElement(new[] { "first", "second" }),
        ["encoding_format"] = JsonSerializer.SerializeToElement("float")
        // Supply dimensions only when supported by your actual upstream model.
    }
}, cancellationToken);
foreach (var vector in result.OpenAiVectors)
{
    // Index identifies the input; response array position need not equal Index.
    Console.WriteLine($"Input {vector.Index}: {vector.Values?.Count} values");
}
```

Default HTTP client name: `Asgard.Loki.Embedding.OpenAI`. API roots include version/tenant paths, which are retained with or without a trailing slash. Roots cannot carry URI credentials, query strings or fragments. API Key is mandatory at adapter construction and excluded from options JSON. Public route names map only to the configured actual model; the SDK never changes model suffixes or consults a model catalog. The native returned `model` remains in `Data`, even when it differs from the configured model recorded in metadata.

## Fidelity and limits

Input forms remain distinct: `[1, 2]` is one token sequence, while `[[1], [2]]` is two inputs. Token IDs must be nonnegative integers; mixed/empty input arrays and empty text are rejected before HTTP. Whitespace is not trimmed and texts are not tokenized, split, normalized, truncated or deduplicated. Model token limits, dimensions support and entitlements are checked by the upstream; no model-specific limit table is embedded.

No encoding or dimension default is injected. Explicit format/dimensions must match the response. Results must have exactly one item per input, unique indices covering that input set, and uniform nonempty vector lengths/encoding. Array order is retained rather than sorted. Missing vectors/indices, duplicate JSON properties, nonfinite JSON numbers, malformed/empty Base64 or incomplete float32 byte groups fail explicitly. Unknown response/request fields, including null/false fields, remain in native JSON. Missing usage stays unknown, never zero-filled.

`Values` is an optional copied `double` view of native JSON numbers. `Base64` remains its original string; `DecodeBytes()` is explicit and returns original bytes. The SDK does not automatically convert Base64 into Float JSON, assume an endianness, inspect encoded floating-point values, normalize vectors or certify semantic correctness. Full native `Data` stays independent of response/document disposal and of these convenience views.

HTTP errors retain complete status/body, including non-JSON bodies; an explicit `error` in a 200 response also fails with its actual status/body. Malformed/truncated JSON, contradictory batch results or SSE are protocol failures. The borrowed `IHttpClientFactory` and adapter remain caller-owned; request-local clients, requests and responses are disposed. Caller-configured HTTP policies must not retry/hedge these POST requests if single-attempt semantics are required.

This is synchronous native multi-input support, not an asynchronous Batch API. Streaming embeddings, Azure-specific authentication, file uploads, vector stores, similarity search, RAG pipelines, background jobs and billing are not implemented here. No real API Key, live HTTP service, Redis or other project's credentials are used by unit tests.

## Jina compatibility audit

Jina's current native `/v1/embeddings` has a reusable **text-only dense subset**, separately covered by three authored full request/response goldens through the public SDK. Configure this adapter with the caller's Jina API root/model/Key and pass Jina-native `embedding_type`, `task`, `late_chunking`, `normalized` and `truncate` in `Parameters`. No organization/project headers are needed unless explicitly configured. Text strings/batches, numeric dense results and Base64 containing complete float32-sized byte groups fit this provider's actual contract. Original returned model, usage, null/false fields and result array order remain in `Data`; use original `Index` for input association.

The audit does not add a vendor adapter, model catalog or automatic field translation. Jina declares `embedding_type`, whereas `encoding_format` is the OpenAI field. Neither is deleted or renamed. If a caller supplies contradictory fields and the returned encoding disagrees with the explicit OpenAI format, validation fails. A Jina-native null `dimensions` does not fit the OpenAI positive-integer option and fails before HTTP rather than being omitted. Use the subset only when its actual input/output requirements match the selected endpoint.

| Native Jina contract | Reuse result / remaining work |
| --- | --- |
| String/string-array input; dense numeric or compatible float32-sized Base64 output | Three full HTTP goldens verify preservation of native settings, indices, values/raw bytes and JSON; model acceptance is unverified |
| TextDoc, ImageDoc, AudioDoc, VideoDoc, standalone PDFDoc and merged-content groups | These native object inputs are rejected by the existing OpenAI input contract; dedicated `Embedding.Jina` work remains |
| Per-token `embeddings` with token strings; sparse maps with or without item indices | The native response shapes do not fit this adapter's dense single-vector schema; they are not flattened or assigned invented indices |
| Binary/ubinary and multiple requested encodings | Native packing/encoding needs independent handling. With declared bit dimensions, packed numeric arrays conflict with OpenAI numeric-element dimensions. Without those dimensions they may pass generic numeric shape validation; that acceptance does **not** certify native bit widths, signedness or semantics |
| PDF/group association and async batch jobs | No page expansion, inferred result count, media fetch or job management is added by this audit |

Nineteen additional offline cases cover those three positive goldens, seven object-input boundaries, five response-shape/dimension boundaries, contradictory format fields, nullable dimensions and two numeric-shape acceptance examples. Sparse cases exercise a published response-schema variant without claiming a particular deployed sparse model or a confirmed New API advantage. The unresolved native formats remain in the [SDK roadmap](../../docs/model-call-roadmap.md); they cannot be closed merely by retaining arbitrary JSON.

Jina contracts checked on 2026-10-19: [official OpenAPI](https://api.jina.ai/openapi.json), version `2026.10.13.1624`, including `EmbeddingsV3Request`, `EmbeddingsV4Request`, `EmbeddingsV5Request`, `SingleEmbeddingData`, `MultiEmbeddingData` and `EmbeddingResponse`, plus the [embedding reference](https://jina.ai/embeddings/). The current public schema was re-fetched and the relevant definitions match the cached audit. This is schema/wire verification with substitute HTTP, without a live embedding, PDF/media interpretation, native packed-format or quality claim.

OpenAI contracts checked: [Embeddings create](https://developers.openai.com/api/reference/resources/embeddings/methods/create), [native embedding type](https://github.com/openai/openai-python/blob/main/src/openai/types/embedding.py), and [official Base64 float32 decoder](https://github.com/openai/openai-python/blob/main/src/openai/lib/_parsing/_embeddings.py). The native wire and output invariants are verified offline; real model acceptance and embedding quality remain unverified.
