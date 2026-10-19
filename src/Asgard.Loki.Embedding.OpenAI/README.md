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
| Offline/live status | 87 substitute-HTTP tests; no live provider/model/vector-quality verification |

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

Contracts checked: [OpenAI Embeddings create](https://developers.openai.com/api/reference/resources/embeddings/methods/create), [native embedding type](https://github.com/openai/openai-python/blob/main/src/openai/types/embedding.py), and [official Base64 float32 decoder](https://github.com/openai/openai-python/blob/main/src/openai/lib/_parsing/_embeddings.py). The native wire and output invariants are verified offline; real model acceptance and embedding quality remain unverified.
