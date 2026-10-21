# Asgard.Loki.Embedding.Azure

Native **deployment-scoped Azure embeddings** for the independent Embedding capability. This pure .NET package references only Embedding, Infrastructure and standard HTTP factory support.

## Support matrix

| Operation | SDK behavior |
| --- | --- |
| `POST {resource}/openai/deployments/{deployment}/embeddings?api-version={version}` | One native JSON request, explicit deployment/version and independent `api-key` |
| Native input | Original scalar/batched text or token arrays; original boundaries, spaces and ordering |
| Output | Complete original `data[]`, indices, float/Base64 vectors, model, usage and unknown fields |
| Batch validation | Every original input index covered exactly once; returned array order stays unchanged |
| Native errors | Actual status, complete copied bytes and content type, including HTTP 200 API errors |

This adapter uses the [dated Azure embeddings REST contract](https://learn.microsoft.com/azure/ai-services/openai/reference#embeddings). It does not automatically select the newer v1 endpoint, an API version, model catalog or Entra/login flow. The configured endpoint must support this deployment/API Key protocol; native model/parameter availability remains with Azure.

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Embedding;
using Asgard.Loki.Embedding.Azure;
using System.Text.Json;

var target = new AzureEmbeddingAdapter(httpClientFactory, new()
{
    Id = "azure-vectors",
    Endpoint = resourceRoot,
    Deployment = actualDeploymentName,
    ApiVersion = selectedNativeApiVersion,
    ApiKey = independentEmbeddingApiKey
    // BodyModel = explicitlyRequiredNativeBodyModel; // Optional, no default.
});
// The application owns the factory; disable retries, hedging and redirects.
await using var loki = new LokiClientBuilder().AddEmbedding(new()
{
    Routes = [new() { Model = "vectors", Targets = [target] }]
}).Build();
var result = await loki.Embedding.EmbedAsync(new()
{
    Model = "vectors",
    Parameters = new()
    {
        ["input"] = JsonSerializer.SerializeToElement(new[] { "First original input", "Second original input" }),
        ["encoding_format"] = JsonSerializer.SerializeToElement("base64"),
        ["dimensions"] = JsonSerializer.SerializeToElement(requestedDimensions)
    }
}, cancellationToken);
// Inspect result.Data as native Azure JSON; preserve each data[].index association.
```

For example, a resource root `https://resource.openai.azure.com/`, deployment `embed.deploy-01` and version `2024-10-21` produce `/openai/deployments/embed.deploy-01/embeddings?api-version=2024-10-21`. Any explicit resource path prefix is retained. The deployment is one escaped path segment, literal dots are preserved, and the version is one escaped query value. No historical date-based deployment rewriting occurs. Root credentials/query/fragments and ambiguous deployment paths fail locally.

Public `Model` is route metadata. The request does not inherit that public name as a native deployment or body model. The optional `BodyModel` adds only the explicitly configured native root `model`; otherwise the field stays absent. Root model overrides, including case aliases, fail rather than changing the fixed target. `Metadata.UpstreamModel` is the original configured deployment; a different resolved model/version in response `Data.model` remains unchanged.

## Native inputs and original results

`Parameters` holds the original native `input`, optional `encoding_format`, `dimensions`, `user` and all unknown fields. Complete JSON is copied before client creation/first await. Text is not trimmed/tokenized, token arrays are not flattened or decoded, and batches are not split. A token ID array is one input; a text array or array of token arrays is a batch. Empty/mixed invalid input, nonpositive dimensions and invalid native encoding names fail explicitly. No model-specific token/dimension limit is guessed, and no default model, encoding, dimensions or user value is inserted.

The full response object remains in `Data`, including precise native JSON numbers, Base64 text, native model/usage/error metadata and unknown fields. Returned `data[]` order is preserved. Its indices must exactly cover this request's inputs without duplicates, missing entries or unrelated indices. The SDK does not sort, zip by position, add indices or synthesize vectors. Each nonempty vector must have a consistent width and format, match explicitly requested dimensions/encoding, and contain valid finite numeric values or valid Base64 with complete float32 byte width. Base64 is validated without automatically decoding/re-encoding values or claiming an endianness/semantic interpretation. Float validation never rewrites the original JSON numbers.

Embeddings return one complete JSON result. Unexpected SSE, non-UTF-8 JSON, duplicate members, wrong containers, inconsistent indices/dimensions/encoding and malformed bodies fail explicitly. No cache, conversation Session, vector database, similarity search, RAG pipeline, background work, model administration or cross-capability state is introduced.

## Ownership, limits and verification

Each call owns its request/client/response. It uses shared bounded HTTP byte reading and waits for complete EOF, declared-length checks, stream/response/client disposal and final cancellation before returning any vectors. Changed response targets or methods fail before accepting another resource's output. Factories remain borrowed. Credentials are independent, omitted from default option JSON, and are not sent as Bearer/organization/project headers. There is no automatic retry, failover, hedging, reconnect or provider switch.

`MaxRequestBytes` defaults to 32 MiB of complete UTF-8 request JSON; `MaxResponseBytes` defaults to 64 MiB of raw result/error bytes. These configurable transport bounds do not establish upstream model acceptance. HTTP/native API errors use `EmbeddingUpstreamException` with actual status, copied original `Body` and `ContentType`; `ResponseBody` explicitly decodes strict UTF-8, so use Body for binary/unknown encodings. Older text-constructed errors retain their original text contract. Errors are never substituted with empty successful vectors or recomputed usage.

Tests use independent native request/result/error vectors and fake HTTP. They cover all four input forms, omitted/explicit body model, exact unknown values, public routing, original vector order and indices, rejected association/format/dimension faults, request snapshots, path/version/auth, native/binary errors and final byte/length/encoding/disposal/cancellation boundaries. **No real Azure resource/deployment, model acceptance, embedding quality or live availability has been verified.** No new live-test credentials or environment variables are introduced.
