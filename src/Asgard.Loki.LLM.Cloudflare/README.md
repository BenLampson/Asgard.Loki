# Asgard.Loki.LLM.Cloudflare

Explicit native Workers AI **`ai/run/{model}` text JSON and SSE**. This package references only LLM, capability-independent Infrastructure and standard HTTP. Audio/Embedding and compatible OpenAI calls use their separate packages/configuration; no peer provider or cross-capability business coordinator is introduced.

## Support matrix

| Operation | Support |
| --- | --- |
| `.LLM.WorkersAiText.CompleteAsync` | Full native result/success/errors/messages JSON envelope with result.response |
| `.LLM.WorkersAiText.StreamAsync` | Original native JSON SSE data plus actual [DONE] control frame, with event/id/retry |
| Prompt/messages, native tools/arguments/usage, response_format, future settings | Original native JSON; no template, association rewrite, usage calculation or model-specific defaults |
| Authentication / target | Explicit API Token Bearer and complete account/run root/model/credential/adapter Session binding |
| Compatible Chat JSON/SSE and non-streaming GPT-OSS Responses | Explicit [existing OpenAI supplier profile](../Asgard.Loki.LLM.OpenAI/README.md#explicit-cloudflare-workers-ai-profile), separate from this native text protocol |
| Other ai/run model dialects, implicit protocol/model detection | Not implemented by this text profile |
| Real account/model/setting/output-quality validation | Not performed; offline native protocol fixtures only |

## Initialization

The application supplies a borrowed IHttpClientFactory, Session store and explicit targets. DistributedCacheLlmSessionStore requires both IDistributedCache and an explicit application-provided first-binding coordinator; ordinary Get/Set is not cross-instance atomic coordination.

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Cloudflare;
using System.Text.Json;

await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore,
    Providers = [new CloudflareTextProvider(httpClientFactory)],
    Credentials = [new LlmCredential("workers-token", apiToken)],
    Routes = [new LlmRoute
    {
        Model = "workers-text",
        Targets = [new LlmTarget
        {
            Id = "workers-text-account", ProviderId = "cloudflare-text",
            BaseAddress = new Uri($"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(accountId)}/ai/run/"),
            UpstreamModel = actualNativeTextModel, CredentialId = "workers-token",
            Protocols = LlmProtocol.WorkersAiText
        }]
    }]
}).Build();
var response = await loki.LLM.WorkersAiText.CompleteAsync(new WorkersAiTextRequest
{
    Model = "workers-text",
    Parameters = new() { ["prompt"] = JsonSerializer.SerializeToElement("Hello") }
}, new LlmCallOptions { SessionId = sessionId }, cancellationToken);
// response.Data retains the entire native HTTP result envelope.
```

## Native semantics and limits

The configured root includes the explicit account and ai/run path or proxy prefix. Actual model namespace separators stay in the URL; individual segments are escaped once. Empty/dot segments, root credentials/query/fragment and malformed Unicode fail before client creation. The public route Model never enters the native body; direct adapter model overrides are also refused. Only method stream mode is set. Original prompt/messages, tool associations/arguments, settings/schema, precise numbers, whitespace, optional null/false/zero and unknown fields remain native. The SDK neither chooses templates nor executes tools.

This explicit text profile requires a full native success envelope with result.response:string, including valid empty text. It does not unwrap/guess a different ai/run model dialect or convert it into Chat/Responses. Native tool names and opaque arguments, original usage and unknown result fields remain intact. Root explicit errors/failed success/nonempty errors retain their failure meaning; nested business error fields do not drive protocol state.

Streaming preserves raw SSE data and immutable optional JSON through WorkersAiTextEvent. Actual [DONE] has Json=null; it is never replaced by a fabricated JSON terminal. Business type/done fields, event names and response text containing the marker do not complete the call. Native tool arguments remain separate; there is no assembly, ID creation, role rewriting or usage total. DONE is protocol termination, not a claim of tool execution or answer quality. Empty generation is allowed without synthetic response text.

The actual DONE waits through normal complete SSE/EOF, strict UTF-8/unique JSON, raw declared-length checks, source/response/client disposal and final cancellation. Repeated DONE, any late data, explicit native error, incomplete events, bad encoding or read/disposal/cancellation failures cannot first publish that terminal. Comments/control-only tails are framed without synthetic messages. Earlier delivered increments may remain after a later failure; early consumer exit closes owned call resources without reading/retrying the rest.

Defaults are 1 MiB request, 32 MiB complete/error response and 64 MiB entire raw SSE, including BOM/comments/control fields/separators. CloudflareTextOptions changes these positive local policies, and all participate in implementation identity alongside the named client. Infrastructure handles raw bounded reading/framing only; native state/error interpretation stays here. Actual URI/method is verified through final resource closure. Factories/cache/coordinators stay borrowed.

Selected Bearer overrides a default Authorization; unbound alternative auth or cf-aig-* gateway headers are refused. Configure named HTTP clients without automatic redirects/retries/hedging to preserve one-attempt fixed-target behavior; change target Version when changing identity-affecting named-client policy. HTTP errors keep actual status, complete original bytes and content type in LlmUpstreamException; native frame errors keep original SSE data bytes rather than a synthesized envelope. Protocol/encoding/resource failures remain explicit failures. No credential discovery/refresh, model catalog, media fetching, server hosting, gateway management, billing, training or background workflow is provided.

References: [native REST](https://developers.cloudflare.com/workers-ai/get-started/rest-api/), [run API](https://developers.cloudflare.com/api/resources/ai/methods/run/), [native SSE](https://blog.cloudflare.com/workers-ai-streaming/) and [compatible endpoint limits](https://developers.cloudflare.com/workers-ai/configuration/open-ai-compatibility/). These describe protocols; selecting a currently available model and verifying actual option acceptance remain application concerns. Offline fixtures are not live service/model validation.

Offline evidence: tests use independent full JSON/SSE windows and fault-injected local HTTP; tests run separately in OpenAI.Tests. Native protocol preservation does not prove live account/model availability, setting acceptance or generated answer/tool quality.
