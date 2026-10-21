# Asgard.Loki.Embedding.Jina

Native Jina dense and per-token multi-vector Embedding provider for the Asgard.Loki .NET SDK. It references only `Asgard.Loki.Embedding` and standard HTTP dependencies. Complete targets and module registration reuse the existing Embedding capability; OpenAI, Rerank, LLM, media-processing and conversational state remain independent.

## Support matrix

| Area | Implemented contract |
| --- | --- |
| Operation | One `POST embeddings` under the configured native API root; default `https://api.jina.ai/v1/` |
| Input | Scalar text/TextDoc/ImageDoc/AudioDoc/VideoDoc or ordered batches of text/native objects |
| Fused content | Ordered nonempty `{content:[...]}` groups inside batches; each group remains one input item |
| Parameters | Native nullable dimensions, task/input type, normalization/truncation/late-chunking flags, single `float`/`base64` encoding and explicit multi-vector/token return settings |
| Output | Required native model/usage/data; indexed `embedding` or per-token `embeddings`, original item order and token strings |
| Views | `result.JinaEmbeddings` contains `JinaEmbeddingItem` with native `Index`, `Data`, `Embedding`, `Embeddings` and `TokenizedInput` |
| Single-vector conveniences | Independent numeric `Values`, original `Base64`, explicit `DecodeBytes()` without endianness inference |
| Validation | Exact input count and unique covering indices; consistent shape/width/encoding; token count/types; finite numeric vectors or complete float32-sized Base64 byte groups |
| Authentication | Explicit API Key/Bearer and caller-owned named `IHttpClientFactory`; no other capability credentials |
| Lifecycle | Native status/body and Jina business errors retained, send/body cancellation and response disposal; no SDK retry/failover |
| Separate work | PDF result/page association, binary/ubinary, multiple encodings, sparse output and asynchronous batch jobs |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Embedding;
using Asgard.Loki.Embedding.Jina;
using System.Text.Json;

var target = new JinaEmbeddingAdapter(applicationHttpClientFactory, new()
{
    Id = "jina-omni",
    Model = "jina-embeddings-v5-omni-small",
    ApiKey = callerProvidedJinaKey
});
await using var loki = new LokiClientBuilder().AddEmbedding(new()
{
    Routes = [new() { Model = "vectors", Targets = [target] }]
}).Build();
var result = await loki.Embedding.EmbedAsync(new EmbeddingRequest
{
    Model = "vectors",
    Parameters = new()
    {
        ["input"] = JsonSerializer.SerializeToElement(new object[]
        {
            new { text = "first passage" },
            new { image = callerProvidedImageReference },
            new { content = new object[] { new { text = "caption" }, new { image = callerProvidedImageReference } } }
        }),
        ["embedding_type"] = JsonSerializer.SerializeToElement("float"),
        ["task"] = JsonSerializer.SerializeToElement("retrieval.passage"),
        ["truncate"] = JsonSerializer.SerializeToElement(false)
    }
}, cancellationToken);
var items = result.JinaEmbeddings; // Use original Index; the fused group remains one input.
// Complete native model, usage and unknown fields remain in result.Data.
```

For a target/model that supports per-token output, explicitly send `return_multivector=true` and optionally `return_tokenized_input=true`. The latter requires the former. Explicit multi-vector mode cannot also request a nonnull `dimensions`, matching the documented v4 option dependency. A native per-token model can return a matrix without that switch; when the switch is absent/null, the native `embedding`/`embeddings` fields determine the shape. No model-name rule or automatic flag is added. Per-token models that accept dimensions may specify them without the explicit v4 multi-vector switch; the server decides its allowed sizes.

## Fidelity and limits

The API root retains version/tenant paths with or without a trailing slash. The configured model is sent unchanged, and metadata keeps it separately from a returned canonical model. The Key is excluded from options serialization. No model lookup, suffix rewrite, environment lookup or implicit authentication fallback occurs.

Native single-modality objects and batch content groups retain all fields, order and Unicode. Each top-level input item expects one indexed result; group chunks are not flattened into separate requests/results. Known oneof fields cannot be combined; group chunks must be native objects, cannot nest groups, and cannot be PDFs. `late_chunking=true` is limited to text input. Media references/Base64 strings are caller-owned native strings; the SDK does not fetch/decode/render them, infer MIME, validate file accessibility or estimate model token limits. Model-specific media, tasks, dimensions and runtime support remain upstream responsibilities. Unknown fields are forwarded, not certified.

The native schema permits empty strings/arrays and an empty per-token sequence. Their shapes remain distinct: an empty string/TextDoc still counts as one input; an empty batch counts as zero; an item with `embeddings:[]` has zero token vectors, not zero input items. The caller still makes an explicit HTTP call, and the server can reject an empty input. The SDK never creates a successful empty result or loading request locally.

Nullable known options remain null; absent options remain absent. Dimensions must be exact positive Int32 values when supplied. The provider does not enforce model-specific maximums, convert `encoding_format` into Jina `embedding_type`, delete conflicting/foreign fields or insert defaults. Only the native `embedding_type` controls response-encoding validation here; an unknown OpenAI alias remains raw upstream data. Native `float`/`base64` requests are supported; explicit binary/ubinary or encoding arrays fail before HTTP with `NotSupportedException` until their packing/association contract is implemented. A standalone PDF also fails before HTTP until page/result association is verified; no guessed page count or hidden resolver is used.

Data must cover the native input set exactly, with unique nonnegative original indices, preserving the returned array order. Each item has exactly one single-vector or multi-vector field, and the batch cannot mix those shapes. Matrix row counts can vary between inputs. Numeric width and encoding must agree across vectors and match explicit settings. A returned token array must have one string per matrix row even when tokens were not requested; when explicitly requested, it cannot be omitted/null. Missing and explicit null token lists remain distinguishable in `Data`. No token strings, indices or source associations are synthesized, and no matrix is flattened or pooled.

Numeric vectors contain finite JSON numbers; nonempty Base64 vectors must contain complete float32-sized byte groups. Encoded floating-point values/endianness are not interpreted or certified. Single-vector `Values` is a copied numeric view; matrix content stays in native JSON. `DecodeBytes()` is explicit for a single Base64 vector; it does not transform `Data`. Raw JSON remains independent of HTTP/document disposal and of convenience-array mutation. Sparse maps and other unimplemented output structures are rejected rather than reindexed or converted into dense vectors.

The native model, `usage.total_tokens` and data array are required. Known token counters are checked as exact nonnegative integers; optional per-modality null counters remain null. Optional fields stay absent and native counters are neither reallocated nor turned into price/billing totals. `error`, `detail` or nonempty `code` failures retain original status/body, even with HTTP 200 and data. Duplicate/truncated/multiple JSON values, SSE/NDJSON and contradictory shape/count/token output fail without retry. Caller-owned adapters, factories and handlers remain borrowed.

There is no async Batch coordinator, PDF parser, media service, vector database, RAG pipeline, shared LLM scheduler, hosted listener, key/channel management or billing system. The [OpenAI-provider Jina-compatible profile](../Asgard.Loki.Embedding.OpenAI/README.md#jina-compatible-profile) remains valid and independently tested; this native supplier adds actual object/group/matrix and nullable-option handling, without referencing that provider.

## Verification

Tests verify text, media, fused groups, float/Base64 matrices, variable token counts, implicit native matrices, nullable/empty shapes, exact counters/indices, unknown fields, malformed/errors/stream responses, request/body cancellation, snapshots and borrowed ownership. `.invalid` references and tiny Base64 strings are transport samples, not renderable-media evidence. Empty and implicit-matrix cases verify declared schema shapes without certifying every model accepts them. No live Jina model, media decoder, packed/sparse output or PDF page association has been verified.

References: [public OpenAPI](https://api.jina.ai/openapi.json), including the v3/v4/v5 and ColBERT requests, document/group types, `SingleEmbeddingData`, `MultiEmbeddingData` and usage schemas; [embedding reference](https://jina.ai/embeddings/).
