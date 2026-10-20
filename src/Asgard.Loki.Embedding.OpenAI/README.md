# Asgard.Loki.Embedding.OpenAI

Native OpenAI Embeddings provider for the Asgard.Loki .NET SDK. It references `Asgard.Loki.Embedding`, capability-independent `Asgard.Loki.Infrastructure` for bounded HTTP reading, and standard HTTP dependencies; it does not install or share LLM/Image providers, Sessions or credentials.

## Support matrix

| Area | Implemented contract |
| --- | --- |
| Operation | One `POST embeddings` under the configured API root |
| Input | Nonempty text, text arrays, one token-ID array, or arrays of token-ID arrays; original order and boundaries retained |
| Parameters | Explicit positive `dimensions`, optional `encoding_format` (`float`/`base64`), `user` and unknown extension JSON |
| Output | Complete native JSON, original `data` order/indices, Float or Base64 embedding values, usage and unknown fields |
| Views | Provider-owned `result.OpenAiVectors`, each with original `Index`, `Data`, `Values` or `Base64`; explicit `DecodeBytes()` |
| Authentication | Fixed API Key/Bearer token, optional organization/project, explicit named HTTP factory |
| Errors/cancellation | Original HTTP status/raw body/content type, 200 business-error detection, final cancellation after resource disposal, no retry or failover |
| Local byte policies | `MaxRequestBytes` defaults to 32 MiB; `MaxResponseBytes` defaults to 64 MiB, including error bodies; no splitting |
| Offline/live status | 163 substitute-HTTP tests, including 19 Jina cases, five canonical M3E cases, seven Workers AI cases, 27 explicit text-profile cases and 18 raw transport/lifetime boundary cases; no live provider/model/vector-quality verification |

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

HTTP errors retain status, original `Body` bytes and `ContentType`, including binary bodies. The existing `ResponseBody` property is a strict UTF-8 projection; it throws for invalid UTF-8 instead of replacing bytes. An explicit `error` in a 200 response also fails with its actual status/body, including a JSON error labeled as plain text. Malformed/truncated JSON, contradictory batch results or SSE are protocol failures. Successful responses must be UTF-8 JSON when a content type is supplied. Unparseable raw content-type headers fail instead of being treated as absent, and HTTP errors retain those header values. Invalid Unicode, duplicate members (including unknown nested fields), exceeded byte limits or contradictory declared length fail. Request inputs are snapshotted before creating an HTTP client. A supplied actual response target/method must match the configured POST endpoint. Authentication and organization/project defaults from the factory are removed; only this target's explicit headers are used. The borrowed `IHttpClientFactory` and adapter remain caller-owned; request-local clients, requests, response streams and responses are disposed before the final cancellation check and result publication. Read/disposal failures cannot publish a successful result. Caller-configured HTTP policies must not retry/hedge these POST requests if single-attempt semantics are required.

This is synchronous native multi-input support, not an asynchronous Batch API. Streaming embeddings, Azure-specific authentication, file uploads, vector stores, similarity search, RAG pipelines, background jobs and billing are not implemented here. No real API Key, live HTTP service, Redis or other project's credentials are used by unit tests.

## Jina compatibility audit

Jina's current native `/v1/embeddings` has a reusable **text-only dense subset**, separately covered by three authored full request/response goldens through the public SDK. Configure this adapter with the caller's Jina API root/model/Key and pass Jina-native `embedding_type`, `task`, `late_chunking`, `normalized` and `truncate` in `Parameters`. No organization/project headers are needed unless explicitly configured. Text strings/batches, numeric dense results and Base64 containing complete float32-sized byte groups fit this provider's actual contract. Original returned model, usage, null/false fields and result array order remain in `Data`; use original `Index` for input association.

The audit does not add a vendor adapter, model catalog or automatic field translation. Jina declares `embedding_type`, whereas `encoding_format` is the OpenAI field. Neither is deleted or renamed. If a caller supplies contradictory fields and the returned encoding disagrees with the explicit OpenAI format, validation fails. A Jina-native null `dimensions` does not fit the OpenAI positive-integer option and fails before HTTP rather than being omitted. Use the subset only when its actual input/output requirements match the selected endpoint.

| Native Jina contract | Reuse result / remaining work |
| --- | --- |
| String/string-array input; dense numeric or compatible float32-sized Base64 output | Three full HTTP goldens verify preservation of native settings, indices, values/raw bytes and JSON; model acceptance is unverified |
| TextDoc, ImageDoc, AudioDoc, VideoDoc and merged-content groups | Rejected by this OpenAI input contract; the independent [native Jina package](../Asgard.Loki.Embedding.Jina/README.md) now implements these forms |
| Per-token `embeddings` with token strings | Not supported here; native Jina now supports Float/Base64 matrices, preserving input indices and token lists |
| Standalone PDFDoc and sparse maps with or without indices | Native association/format work remains separate; no guessed page count, flattening or invented indices |
| Binary/ubinary and multiple requested encodings | Native packing/encoding needs independent handling. With declared bit dimensions, packed numeric arrays conflict with OpenAI numeric-element dimensions. Without those dimensions they may pass generic numeric shape validation; that acceptance does **not** certify native bit widths, signedness or semantics |
| PDF/group association and async batch jobs | No page expansion, inferred result count, media fetch or job management is added by this audit |

Nineteen additional offline cases cover those three positive goldens, seven object-input boundaries, five response-shape/dimension boundaries, contradictory format fields, nullable dimensions and two numeric-shape acceptance examples. These remain valid OpenAI-provider boundaries after the independent native Jina supplier was added.

Jina contracts checked on 2026-10-19: [official OpenAPI](https://api.jina.ai/openapi.json), version `2026.10.13.1624`, including `EmbeddingsV3Request`, `EmbeddingsV4Request`, `EmbeddingsV5Request`, `SingleEmbeddingData`, `MultiEmbeddingData` and `EmbeddingResponse`, plus the [embedding reference](https://jina.ai/embeddings/). The current public schema was re-fetched and the relevant definitions match the cached audit. This is schema/wire verification with substitute HTTP, without a live embedding, PDF/media interpretation, native packed-format or quality claim.

OpenAI contracts checked: [Embeddings create](https://developers.openai.com/api/reference/resources/embeddings/methods/create), [native embedding type](https://github.com/openai/openai-python/blob/main/src/openai/types/embedding.py), and [official Base64 float32 decoder](https://github.com/openai/openai-python/blob/main/src/openai/lib/_parsing/_embeddings.py). The native wire and output invariants are verified offline; real model acceptance and embedding quality remain unverified.

## Canonical M3E text profile

A service exposing canonical `POST embeddings` with text/string-array `input`, Bearer authentication and indexed numeric `data` can use the same adapter. Configure a distinct `Id`, explicit caller-owned `BaseAddress`, actual `Model`, `ApiKey` and `HttpClientName` as in initialization above. No Moka API root or model name is supplied by this profile. The operation is selected by the adapter, without routing on an `m3e` model prefix. Organization/project remain absent unless explicitly configured.

Five offline profile cases verify whole scalar/batch requests and native responses, original whitespace/Unicode, reordered indices, exact JSON numbers, optional missing root/item fields, missing usage, native returned model, parameter snapshots, errors and duplicate-index rejection. Strings are not coerced to arrays, non-string elements are not filtered, indices are not fabricated and returned models are not overwritten. This profile covers only canonical text and numeric dense output; it does not certify M3E token inputs, Base64, multimodal contracts or a specific hosted endpoint.

The [official M3E model card](https://huggingface.co/moka-ai/m3e-base) describes model weights and local inference; it is not evidence for an HTTP service contract. No hosted Moka availability, live model acceptance or embedding quality has been verified.

## Workers AI text profile

Cloudflare documents an [OpenAI-compatible Embeddings endpoint](https://developers.cloudflare.com/workers-ai/configuration/open-ai-compatibility/). Configure this adapter with your explicit account-scoped API root, independently supplied token, actual embedding model and target/client identities:

```csharp
var target = new OpenAiEmbeddingAdapter(httpClientFactory, new OpenAiEmbeddingOptions
{
    Id = "cloudflare-vectors",
    BaseAddress = accountScopedEmbeddingRoot,
    Model = cloudflareEmbeddingModel,
    ApiKey = cloudflareToken,
    HttpClientName = "cloudflare-embedding"
});
```

`accountScopedEmbeddingRoot` must be a caller-created URI such as `https://api.cloudflare.com/client/v4/accounts/<account_id>/ai/v1/`, with the actual account substituted. The provider appends only `embeddings`; it preserves proxy/account prefixes. Pass native text/string-array `input`. The model stays in the request body, including names containing `@` and `/`; the SDK performs no model-prefix routing or catalog lookup. Native unknown JSON is retained, but passing an extension does not certify model acceptance.

Seven independent offline cases cover both text forms, roots with/without a trailing slash, complete account-scoped requests/Bearer headers, original indexed numeric vectors/native model/missing usage, errors and rejection of a different operation's output. The separate `ai/run/<model>` API with a `result.shape/data` wrapper is not implemented by this profile; it is not flattened or given synthetic indices. Token input, Base64, model-specific dimensions/options and native `ai/run` semantics are not certified here. No Vectorize/vector database, Worker hosting, AI Gateway configuration, automatic credential discovery or live embedding/model/quality verification is included.

## Explicit compatible text targets

The same adapter also has independent scalar/text-batch fixtures for these explicit root shapes. Supply your actual host, API root, model, credential, target Id and named client; the SDK does not recognize brands, select endpoints from a model name or install a separate vendor wrapper. This is a verified canonical text/indexed-numeric protocol subset, not a model catalog or proof of a hosted service's availability.

| Explicit profile | API-root path shape before embeddings |
| --- | --- |
| Ark text embedding | `/api/v3/` |
| DashScope compatible text embedding | `/compatible-mode/v1/` |
| SiliconFlow compatible text embedding | `/v1/` |
| Zhipu v4 text embedding | `/api/paas/v4/` |
| Moonshot-style compatible target | `/v1/`; hosted embedding availability remains unverified |
| OpenRouter-compatible target | `/api/v1/` |
| Xinference-compatible target | `/v1/` |
| Existing compatible upstream | Caller-defined root, including proxy prefixes |
| Explicit custom compatible target | Caller-defined root, including tenant/version prefixes |

Twenty-seven cases cover each target's complete scalar/batch request, fixed Bearer/client/model identity and complete errors. They preserve native response model, reordered indices, exact JSON numbers, unknown/null/false fields and missing usage. The profile adds no option aliases, missing defaults, model rewriting, prompt processing or document association table. Use the generic initialization above with your selected explicit configuration. API Key remains mandatory here, including for configured self-hosted Bearer targets; no anonymous authentication is implied by a profile name. Arbitrary route/auth templates do not automatically fit this provider.

Native model-specific dimensions, encoding formats, extra settings, token-ID acceptance, multimodal embedding endpoints, asynchronous Batch operations and actual text/vector semantics remain separate from this tested subset. Passing unknown JSON demonstrates wire retention, not upstream acceptance. Additional native contract references are [Ark Embeddings](https://api.volcengine.com/api-docs/view?action=Embeddings&serviceCode=ark&version=2024-01-01), [DashScope compatible embedding](https://www.alibabacloud.com/help/en/model-studio/embedding) and [Zhipu v4 embedding](https://docs.bigmodel.cn/api-reference/模型-api/文本嵌入). No real server/model call has been made for these fixtures.
