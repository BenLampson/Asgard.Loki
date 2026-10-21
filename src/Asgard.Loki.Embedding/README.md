# Asgard.Loki.Embedding

Independent embedding module for the Asgard.Loki .NET SDK. It references only `Asgard.Loki.Abstractions`, and adds `AddEmbedding(...)` / `loki.Embedding.EmbedAsync(...)` through the existing module mechanism. No LLM, Audio, Image or other provider package is installed transitively.

## Current support

| Concern | Contract |
| --- | --- |
| Registration | Eager configuration validation and collection snapshots; one lazy module per client |
| Targets | Each borrowed `IEmbeddingAdapter` encapsulates its own server, actual model, credentials and native dialect |
| Routing | Exact public model names; independent round-robin selection per route and per client |
| Request | Public `Model` plus native JSON `Parameters`; only root `model` is reserved |
| Result | Complete independently owned native `Data` and public/upstream/provider metadata |
| Errors/cancellation | Propagated from the selected adapter, without retries or target failover |
| Bundled providers | [Embedding.OpenAI](../Asgard.Loki.Embedding.OpenAI/README.md), [Embedding.Google](../Asgard.Loki.Embedding.Google/README.md), [Embedding.Ollama](../Asgard.Loki.Embedding.Ollama/README.md), [Embedding.Jina](../Asgard.Loki.Embedding.Jina/README.md) and [Embedding.Azure](../Asgard.Loki.Embedding.Azure/README.md) document their own native HTTP operations and supplier-specific result handling |

```csharp
using Asgard.Loki;
using Asgard.Loki.Embedding;
using System.Text.Json;

IEmbeddingAdapter target = applicationOwnedEmbeddingAdapter; // Your complete native target.
await using var loki = new LokiClientBuilder()
    .AddEmbedding(new EmbeddingOptions
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
    }
}, cancellationToken);
// result.Data is the unmodified native response; this example assumes your adapter's input dialect.
```

Core does not infer an input shape, tokenize text, normalize vectors, reorder batch results, invent missing indices/usage, change encoding or validate a supplier's model limits. Each native provider must validate its own successful/error response and original index association; retaining an arbitrary JSON object here is not a claim that it is a valid OpenAI/Google embedding response. Provider-specific convenience views belong to the provider package. Changing a target's native dialect does not convert a request automatically.

Only one explicit native operation is called. Synchronous multi-input requests are not an asynchronous Batch service. There is no conversational Session, cache, shared LLM scheduler, vector database, search engine, RAG pipeline, tool executor, automatic chunking, background job, billing or HTTP listener. Adapters remain caller-owned after client disposal. Do not mutate request dictionaries concurrently while a call snapshots them; once dispatched, subsequent source dictionary/document changes cannot alter the adapter's copy.

The separately tested native providers do not change these core contracts. Tests use custom in-memory adapters to verify native JSON, configuration/request snapshots, exact and concurrent routing, cancellation, failure propagation and borrowed ownership. They do not verify an upstream embedding model, HTTP protocol or vector quality. No live embedding call has been made.

## Original upstream error bytes

`EmbeddingUpstreamException` retains the original `(message, statusCode, responseBody)` constructor and its text/status properties. The additive byte-body constructor preserves actual status, complete copied `Body` bytes and original `ContentType`, including binary errors and native API errors carried by HTTP 200. Legacy text construction keeps its original text and exposes a UTF-8 byte projection without inventing a content type. For a byte-constructed error, `ResponseBody` explicitly decodes strict UTF-8; inspect `Body` for unknown/binary encodings. No replacement characters or success/empty-vector result are substituted for failures. The core does not interpret supplier error fields.
