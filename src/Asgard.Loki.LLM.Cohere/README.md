# Asgard.Loki.LLM.Cohere

`LlmTarget.AllowedOutputFormats[LlmProtocol.CohereChat]` distinguishes JSON mode from Schema intent: native `response_format.type = "json_object"` with `schema` requires `json_schema` permission while retaining that original type. SDK complete/streaming calls reject disallowed formats before invocation. [Cohere's model and RAG/tool restrictions](https://docs.cohere.com/v1/reference/chat) still apply independently. See the [format guide](../../docs/llm-output-formats.md).

Native Cohere **v1 `POST chat` JSON and default newline-delimited streaming**. The package references only LLM, capability-independent Infrastructure and standard HTTP support. Rerank.Cohere remains a separate capability/provider with independent credentials and state.

## Support matrix

| Operation | Support |
| --- | --- |
| `.LLM.CohereChat.CompleteAsync` | Complete native JSON, including valid empty tool-planning text and optional IDs/finish_reason |
| `.LLM.CohereChat.StreamAsync` | Default NDJSON; native stream-start, text/search/citation/tool events, stream-end and future metadata |
| History, preamble, documents, citations and tools/results | Original native JSON, order, call parameters, result arrays and unknown fields |
| Tool delta indices and parameter strings | Preserved per event; no assembly, ID synthesis or execution |
| Native settings / structured response_format | Explicit extension JSON; no generation defaults, schema conversion or model acceptance promise |
| Authentication / routing | Explicit API Key Bearer, configured actual model and complete API root/Session target |
| Optional SSE via Accepts header, v2 Chat, implicit conversion | Not implemented |
| Live model/service/account validation | Not performed; offline protocol fixtures only |

## Initialization

Supply a borrowed `IHttpClientFactory`, an `ILlmSessionStore` and explicit targets. The distributed-cache store also requires an application-provided first-binding coordinator; ordinary Get/Set is not cross-instance atomic coordination. See the [root initialization](../../README.md).

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Cohere;
using System.Text.Json;

await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore,
    Providers = [new CohereLlmProvider(httpClientFactory)],
    Credentials = [new LlmCredential("cohere-key", apiKey)],
    Routes = [new LlmRoute
    {
        Model = "my-cohere",
        Targets = [new LlmTarget
        {
            Id = "cohere-native", ProviderId = "cohere",
            BaseAddress = new Uri("https://api.cohere.com/v1/"),
            UpstreamModel = actualSupportedModel, CredentialId = "cohere-key",
            Protocols = LlmProtocol.CohereChat
        }]
    }]
}).Build();
var response = await loki.LLM.CohereChat.CompleteAsync(new CohereChatRequest
{
    Model = "my-cohere",
    Parameters = new() { ["message"] = JsonSerializer.SerializeToElement("Hello") }
}, new LlmCallOptions { SessionId = sessionId }, cancellationToken);
// response.Data contains the complete original native object.
```

## Native semantics and limits

The root is the explicit versioned API root, including any proxy prefix; only `chat` is appended. The selected actual model and method-owned stream switch are the only mandatory body rewrites. Every prior USER/SYSTEM/CHATBOT turn, native tool definition, call-plus-parameters tool result, document, exact number, optional null/false/zero and future field remains native. No max_tokens, safety_mode, history rewrite, conversation loop or server-side conversation management is added. Returned native response_id and generation_id remain separate and never become Responses continuation ownership.

The current v1 stream uses event_type=stream-end and does not require the legacy is_finished flag. If present, that flag must agree with event_type. Known citation/search/tool events require their native array/object carriers; tool calls keep name plus parameter objects, while optional delta indices and string fragments remain untouched. A single stream-start with stable generation_id and a single stream-end with full response/text and native finish_reason are required. Optional end response generation_id/finish_reason must agree when present; response_id stays independent. Native ERROR/ERROR_LIMIT/ERROR_TOXIC/MAX_TOKENS and future finish reasons remain unchanged. IsTerminal denotes protocol termination, not successful generation, tool execution or answer quality. Unknown metadata and business type/error fields inside nested data remain raw. No SSE identity, DONE, tool ID or usage total is invented.

From the first stream-end onward records are held through normal EOF, strict UTF-8/JSON, raw length checks, owned stream/response/client disposal and final cancellation. Duplicate starts/ends, late known generation, inconsistent IDs/reasons/legacy flags, explicit root errors, malformed tails, read/disposal faults and cancellation fail without first exposing that terminal. Earlier delivered increments may remain. Empty whitespace lines and a nonempty final record without LF are accepted; arbitrary network/Unicode fragmentation is supported.

Defaults are 1 MiB request, 32 MiB complete/error body, 4 MiB record (includes CR, excludes LF), 64 MiB total stream (all delimiters included), and 128 buffered terminal/tail records. `CohereLlmOptions` explicitly changes these local policies and optional X-Client-Name; all participate in provider binding identity. Oversize data fails without truncation or retry. Borrowed factory/cache/coordinator survive calls; per-call HTTP resources are owned. Selected Bearer overrides factory Authorization; unbound alternative auth, Accepts SSE negotiation and implicit project headers are refused. Named clients must not add retries or redirects if the application requires the SDK's one-attempt fixed-target contract.

HTTP failures retain actual status, original body bytes and content type in LlmUpstreamException, including binary errors; native root error records also retain raw bytes. The SDK does not fetch documents/media, execute tools, install models, probe model availability, refresh credentials, retry or fail over. A documented endpoint/model or deprecated option is not a live support guarantee; callers select a currently available model and native options.

References: [v1 Chat](https://docs.cohere.com/v1/reference/chat), [streaming](https://docs.cohere.com/v1/reference/chat-stream), [deprecations](https://docs.cohere.com/docs/deprecations). Offline tests verify full literal payload/event windows and failure boundaries with fake HTTP and MemoryDistributedCache, not live credentials, service acceptance or model output quality.
