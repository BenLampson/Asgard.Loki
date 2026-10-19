# Asgard.Loki.Embedding.Ollama

Native Ollama Embedding provider for the Asgard.Loki .NET SDK. It references only `Asgard.Loki.Embedding` and standard HTTP dependencies. It uses the existing module registration and stateless complete targets; LLM, conversion, other capability/provider packages and conversational Sessions are independent.

## Support matrix

| Area | Contract |
| --- | --- |
| Operation | One `POST embed` under the configured native API root; default `http://localhost:11434/api/` |
| Text input | Native scalar text or ordered text arrays; no tokenization, splitting or automatic truncation |
| Media input | Native objects with `text`, `image` and/or `audio`; mixed text/object batches; caller-provided standard Base64 media |
| Settings | `dimensions`, `truncate`, `keep_alive`, runtime `options`, `runner` and unknown JSON retain their original values |
| Empty input | Missing/null/empty scalar/empty array input permits a zero-vector native response; batch empty strings each remain one input item |
| Response | Complete native JSON, returned model, ordered `embeddings`, optional nanosecond durations/token count and unknown fields |
| Views | `result.OllamaEmbeddings` with independent array `Data` and finite numeric `Values`; no invented upstream index |
| Authentication | No added authentication by default; optional explicit Bearer `ApiKey` and caller-owned named `IHttpClientFactory` |
| Failures | HTTP status/body and 200 business errors retained; malformed, truncated, duplicate-property, stream or inconsistent vector responses rejected |
| Ownership | Cancellation reaches send/body reads; responses disposed, borrowed factory/handler ownership retained |

```csharp
using Asgard.Loki;
using Asgard.Loki.Embedding;
using Asgard.Loki.Embedding.Ollama;
using System.Text.Json;

IHttpClientFactory httpFactory = applicationHttpClientFactory;
var target = new OllamaEmbeddingAdapter(httpFactory, new()
{
    Id = "local-vectors",
    Model = "embeddinggemma",
    BaseAddress = new("http://localhost:11434/api/")
    // ApiKey = callerProvidedKey // Only for a target that requires Bearer authentication.
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
        ["input"] = JsonSerializer.SerializeToElement(new[] { "first", "second" }),
        ["truncate"] = JsonSerializer.SerializeToElement(false),
        ["keep_alive"] = JsonSerializer.SerializeToElement("10m")
    }
}, cancellationToken);
var vectors = result.OllamaEmbeddings; // Original array order corresponds to native input order.
// result.Data retains the native model, durations, counters and future fields.
```

The root already contains `/api/`; the adapter appends only `embed`, preserving any tenant prefix with or without a trailing slash. Model namespace/tag/digest strings are sent exactly as configured. The metadata exposes the configured target model separately from the native returned model; no alias/tag normalization or model lookup is performed. A null Key adds no Authorization header; a supplied Key must be nonblank and is excluded from options serialization. There is no implicit environment lookup or cloud login. Configuring Bearer authentication does not certify a hosted endpoint supports this embedding operation.

## Validation and limits

Known text/media containers, native integer dimensions and nullable setting types are checked before HTTP. Positive dimensions require each returned vector to have that exact length. Zero/negative dimensions retain the native non-reduction behavior, without injecting or rewriting a setting. Without an explicit positive dimension, nonempty vectors must have consistent dimensions within the batch. The vector count must exactly match the native input count. These checks cannot prove an upstream vector actually represents its associated text or that a server has respected a requested truncation setting.

The native zero-input response is distinct from generating an embedding for an empty batch entry. No load/unload, warm-up or management request is made automatically. The caller may explicitly issue that native empty request through this operation. A single normal input returning zero vectors is an error. The provider adds no absent durations or counters; present counters must be nonnegative Int64 JSON integers and retain precision beyond `2^53`. No total-token field, usage envelope, score or vector normalization is synthesized.

Image/audio Base64 transport is validated without interpreting the file format, guessing MIME, downloading a URL or borrowing credentials. Unknown request fields are forwarded; this is not proof that a model/server accepts them. Current `video` input is explicitly unsupported. Model-specific media, runner, runtime-option and duration-string acceptance remains the upstream server's responsibility; the SDK does not reproduce Go duration parsing, list models, repair fields or silently remove unsupported settings.

Only the native `/api/embed` response is supported here. The deprecated `/api/embeddings` prompt/single-vector dialect and OpenAI-compatible `/v1/embeddings` are separate contracts; use the OpenAI provider only where its actual wire contract matches. There is no automatic dialect switch, model pull/install, process launch, server/listener, SDK-side retry/failover, media processing, vector database, RAG pipeline, background execution or billing.

## Verification

Offline xUnit v3 tests use substitute HTTP and independently authored complete request/response fixtures for scalar text, ordered batches, image, audio, mixed inputs and explicit empty calls. They cover final endpoint/authentication/model/parameters, source snapshots, Unicode, nullable/default distinctions, vector counts/dimensions/numbers, exact counters, malformed/errors/stream responses, cancellation and borrowed ownership. Base64 samples are opaque transport bytes, not evidence that an image/audio decoder or live model accepts them. No live Ollama or hosted model has been called.

Contracts checked on 2026-10-19: [native embed reference](https://docs.ollama.com/api/embed), [Ollama API types v0.40.6](https://pkg.go.dev/github.com/ollama/ollama@v0.40.6/api#EmbedRequest), [versioned native handler](https://github.com/ollama/ollama/blob/v0.40.6/server/routes.go#L1014), [authentication](https://docs.ollama.com/api/authentication) and [errors](https://docs.ollama.com/api/errors). The current API types/handler include media/empty-input behavior beyond the text-focused reference page; native shape support does not certify every installed Ollama version or the pinned New API gateway supports all newer fields.
