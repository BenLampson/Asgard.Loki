# Asgard.Loki.Embedding.Google

Native Google Gemini Developer API embeddings for the Asgard.Loki .NET SDK. This package references only `Asgard.Loki.Embedding` and standard HTTP components. No OpenAI/LLM provider, conversational Session or shared credential configuration is required.

## Support matrix

| Area | Implemented contract |
| --- | --- |
| Operations | Explicit fixed-target `EmbedContent` (default) or synchronous `BatchEmbedContents` |
| Endpoint | Configured version/tenant root + `models/{actual-model}:embedContent` / `:batchEmbedContents` |
| Authentication | Caller-provided `x-goog-api-key`; key is never added to the URI |
| Input | Native `content.parts`, including caller-supplied multimodal/reference JSON; no conversion or file fetch |
| Settings | Original legacy fields, current `embedContentConfig`, ProtoJSON field aliases and unknown JSON retained |
| Batch | Each item bound to the same actual model; per-item tasks/titles/dimensions retained independently |
| Output | Original `embedding` / ordered `embeddings`, finite numerical `values`, raw metadata/usage/unknown fields |
| Views | Provider-owned `result.GoogleEmbeddings` / `GoogleEmbeddingVector`, without invented `index` fields |
| Validation | Complete count/order-position association and explicitly declared per-item dimensions; malformed/error responses fail |
| Verification | tests; no live model or vector-quality verification |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Embedding;
using Asgard.Loki.Embedding.Google;
using System.Text.Json;

// Supply your own HTTP factory, API Key, bare actual model ID and cancellation token.
var target = new GoogleEmbeddingAdapter(httpClientFactory, new GoogleEmbeddingOptions
{
    Id = "google-vector-batch",
    Model = configuredGoogleModel, // bare ID, without "models/"
    ApiKey = apiKey,
    Operation = GoogleEmbeddingOperation.BatchEmbedContents
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
        ["requests"] = JsonSerializer.SerializeToElement(new[]
        {
            new { content = new { parts = new[] { new { text = "first" } } } },
            new { content = new { parts = new[] { new { text = "second" } } } }
        })
    }
}, cancellationToken);
// Position follows the original requests array. Google does not return OpenAI-style indices.
var firstValues = result.GoogleEmbeddings[0].Values;
```

For a single Content, configure `Operation = EmbedContent` and pass its native `content` object rather than `requests`. Several parts in one Content remain one input/vector; they are not split into a batch. Configure separate aliases/targets for different native operations or dialects. Default root is `https://generativelanguage.googleapis.com/v1beta/`; default HTTP client name is `Asgard.Loki.Embedding.Google`. Roots retain tenant/version paths and cannot contain credentials, queries or fragments. The adapter validates the required Key at construction and excludes it from options JSON. Vertex OAuth/project/location endpoints require a separate provider.

## Native fidelity and boundaries

Only the configured operation determines the method. Public routing `Model` is not a Google body field: the actual model goes in the path. A missing batch-item `model` is filled with that target's exact `models/{actual-model}` resource. An explicit item model must already equal it; mismatches, nulls and bare references fail before HTTP. Unknown nested `model` metadata is never scanned or rewritten. The caller's source request is not mutated.

Legacy `taskType`, `title` and `outputDimensionality` remain in their original envelope. Current `embedContentConfig` and snake-case ProtoJSON aliases remain where supplied; no automatic migration, task inference, prompt insertion or setting default occurs. Explicit null/unset stays in JSON. Positive int32 output dimensions, including exact numeric-string/exponent forms, are read without rounding. Different batch items may request different dimensions. Conflicting legacy/current dimensions or simultaneous aliases of the same dimension/config field fail rather than choosing precedence. Other model-specific task/media/title/truncation rules are left to the configured upstream; transmitting them is not proof that the model honored them.

Responses must match the single/batch envelope and input count. Each vector must have nonempty finite numerical values and match its own explicit dimension. Google returns batch results in request order; the SDK preserves that order and does not fabricate IDs, sort vectors, or fill missing values. Matching dimensions catch some swapped results, but cannot independently verify semantic identity when an upstream violates its order contract. Numeric ProtoJSON strings retain their original JSON; `Values` is a copied double view. Unknown `shape`, statistics and usage metadata remain raw, missing usage stays unknown, and zero counts/false flags remain unchanged. First-party shape-only tensors without public numerical `values` are outside this API-key Developer API view and fail explicitly.

HTTP failures retain complete status/body; a 200 business error also fails with its actual status. Invalid/truncated JSON, duplicate properties, missing/extra results, conflicting envelopes, nonfinite numbers and dimension mismatches cannot become successful empty embeddings. Unexpected SSE is rejected at headers before reading its body. Cancellation is forwarded to HTTP/body reading. The factory and adapters remain borrowed; request-local clients/requests/responses are disposed. No SDK retry, failover, tokenization, input splitting, normalization, default truncation change, hidden upload/download, similarity search, vector store, RAG service or billing logic is added. Caller-configured HTTP policies must also avoid automatic POST retries if single-attempt semantics are required.

`BatchEmbedContents` is one synchronous native HTTP request. Asynchronous `asyncBatchEmbedContent`, hosted batch jobs, streaming embeddings, Vertex authentication and model discovery are not implemented here. Six independent full wire goldens cover legacy/current/alias settings, per-item association and multimodal transport. Fixture image bytes and file URLs are protocol samples only: no media parser, file fetch, live model, API Key or Redis is exercised. These tests do not certify actual task effects, input acceptance or vector quality.

Contracts checked: [Google embeddings REST](https://ai.google.dev/api/embeddings), [current embedding input guide](https://ai.google.dev/gemini-api/docs/embeddings), [official protobuf model/order contract](https://github.com/googleapis/googleapis/blob/master/google/ai/generativelanguage/v1beta/generative_service.proto), and [ProtoJSON representation/null rules](https://protobuf.dev/programming-guides/json/). Current REST documentation includes newer configuration/metadata fields beyond the public protobuf snapshot; native JSON is retained rather than restricted to that older snapshot.
