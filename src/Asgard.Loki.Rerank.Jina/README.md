# Asgard.Loki.Rerank.Jina

Native Jina `POST rerank` for the Asgard.Loki .NET SDK. The package references only `Asgard.Loki.Rerank` and standard HTTP dependencies. It does not reference Cohere, Embedding, LLM or a shared retrieval/agent coordinator.

## Support matrix

| Native surface | Support |
| --- | --- |
| Text requests | String query; nonempty document array containing strings or `TextDoc` objects |
| Multimodal requests | String or `ImageDoc` query; strings, `TextDoc` and `ImageDoc` candidates, with native URLs/Base64 kept unchanged |
| Listwise settings | Native `max_doc_length` and `return_embeddings`; returned vectors stay attached to their original ranked items |
| Nullable settings | `top_n`, `return_documents`, `return_embeddings`, `max_doc_length` preserve null versus false/omission |
| Results | Complete native model/object/usage/results and unknown fields; original input indices, finite scores, optional native documents/vectors |
| HTTP/errors | One request, native status/body in `RerankUpstreamException`, no retry/failover |
| Streaming | No native SSE operation; successful SSE rejected at headers before reading its body |

The configured actual model selects which native request form the upstream accepts. The SDK does not maintain a model-name catalog, infer modalities from names or switch models. Accepting a documented input shape locally does not establish model/account support. The verified native schema is Jina OpenAPI version `2026.10.13.1624`.

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Rerank;
using Asgard.Loki.Rerank.Jina;
using System.Text.Json;

var adapter = new JinaRerankAdapter(httpClientFactory, new()
{
    Model = "your-authorized-jina-reranker",
    ApiKey = apiKey,
    BaseAddress = new Uri("https://api.jina.ai/v1/")
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
var ranking = result.JinaRankings[0];
var originalInputIndex = ranking.Index; // Not the returned array position.
var nativeDocument = ranking.Document;
```

`BaseAddress` includes the version path and may include a tenant/proxy prefix; `rerank` is appended without removing it. `HttpClientName` selects the caller's named HTTP client. Bearer credentials are runtime-validated and excluded from options JSON. Requests/clients/responses are request-local, while the factory and adapter remain borrowed. No conversational Session or cache is required.

## Native fidelity and limits

Only the public root model is replaced. Parameters, image query and documents are snapshotted before HTTP awaits, including direct adapter calls. No default field, media fetch/decode, text normalization, object-to-text conversion, query rewrite, truncation, splitting, sorting or score normalization is performed. Document/query objects carrying both `text` and `image` are explicitly rejected rather than silently selecting a union branch. Unknown fields are forwarded and returned fields retained, without claiming that a particular upstream accepts or echoes extensions.

`top_n` is null or an exact positive integer, including values exceeding Int32; the original JSON spelling is retained. `max_doc_length` follows the current native 1–8192 bound or null. Omitted/null top-n expects all documents; otherwise the result count is `min(top_n, document count)`. Returned indices must be unique and belong to the original candidate array. The SDK rejects missing/nonfinite scores but does not impose Cohere's zero-to-one range on Jina scores.

Omitted `return_documents` uses the native response default (documents required) without writing `true` into the request. Explicit true also requires them. False/null does not require a returned document, but any returned content is validated against its indexed original text/image, preserving the native string/object container. A bare returned string can be matched to the indexed original image content; an explicitly typed object must also match its modality. Explicit null is not silently interpreted as true. Unknown input object fields have no guaranteed upstream echo; they are never invented in the reply. Optional embeddings must be nonempty finite numeric arrays when present; explicit `return_embeddings=true` requires one for every returned item. No shared vector model or Embedding call is introduced.

The native response requires a string model and usage with exact nonnegative integer `total_tokens`. These counters may exceed Int32 and remain raw JSON; absent usage is not inferred or filled with zero. Optional `object`, when present, must be `list`. Unknown usage/response/item fields and original numeric precision stay in `Data`. Provider-owned `JinaRankings`/`JinaRerankItem` expose convenient copies; double views have ordinary floating-point precision. A manually constructed view lacks the original request and cannot establish its document association or expected result count.

## Validation

The tests use substitute HTTP handlers and fragmented in-memory UTF-8 streams, never real API Keys, Redis or provider calls. Seven independently authored full-wire fixtures cover native text/object, nullable/suppressed options, text/image queries with multimodal candidates, and listwise vectors. Negative and lifecycle tests check shape/association/count/usage, exact integers, malformed/truncated/duplicate JSON, native errors, successful SSE refusal, snapshots, cancellation and ownership. These do not establish real ranking quality or live model acceptance. Development validation is local; GitHub CI is reserved for PR/release gates.

References: [Jina API documentation](https://api.jina.ai/docs), [official OpenAPI contract](https://api.jina.ai/openapi.json), [Jina Reranker](https://jina.ai/reranker/).
