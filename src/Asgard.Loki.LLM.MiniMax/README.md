# Asgard.Loki.LLM.MiniMax

This provider's Chat SDK calls honor complete-target `AllowedOutputFormats[LlmProtocol.ChatCompletions]` for the native `response_format` envelope; declare only formats actually accepted by the selected v2 endpoint/model. The separate Messages service requires its own declaration: thinking/effort support is not a Schema capability claim. See the [format support guide](../../docs/llm-output-formats.md).

Native MiniMax v2 Chat JSON/SSE through the existing LLM ChatCompletions SDK contract. This provider calls `text/chatcompletion_v2` under the complete configured root. It references only LLM, Infrastructure and standard HTTP support; it has no Image, Audio, Video or supplier-peer dependency.

## Support matrix

| Operation | Support |
| --- | --- |
| Native v2 complete Chat | Full original JSON, choices/messages, reasoning/tool data, sensitivity flags and usage |
| Native v2 streaming Chat | Original SSE JSON/event/id/retry; native finish chunks and optional complete message snapshot |
| Authentication | Explicit independently selected API Key, request-local Bearer |
| Sessions | Existing complete server/model/credential/provider/client/policy/version binding |
| Native Messages | Use the [existing Anthropic package](../Asgard.Loki.LLM.Anthropic/README.md#explicit-minimax-messages) with a separate complete target |
| Responses, compaction, Realtime and text/FIM | Not implemented by this provider |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.MiniMax;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedNativeModel and selectedApiKey are caller-owned.
var provider = new MiniMaxLlmProvider(factory);
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("native-key", selectedApiKey)],
    Routes = [new LlmRoute
    {
        Model = "native-chat", Targets = [new LlmTarget
        {
            Id = "native-primary", ProviderId = provider.Id,
            BaseAddress = new Uri("https://api.minimax.io/v1/"),
            UpstreamModel = selectedNativeModel, CredentialId = "native-key",
            Protocols = LlmProtocol.ChatCompletions
        }]
    }]
}).Build();
var result = await loki.LLM.ChatCompletions.CompleteAsync(new ChatCompletionRequest
{
    Model = "native-chat",
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" })
}, new LlmCallOptions { SessionId = "conversation-1" }, cancellationToken);
// result.Data contains the complete native JSON.
// ChatCompletions.StreamAsync returns unassembled original native events.
```

The default provider ID is `minimax`; named client is `Asgard.Loki.LLM.MiniMax`. Explicit China and proxy roots are supported without hostname or region inference. Configure each platform's actual model and independent Key. The provider appends only `text/chatcompletion_v2`; it never silently changes to `/chat/completions` or rewrites a model name. The [official v2 reference](https://platform.minimax.io/docs/api-reference/text-post) currently labels this endpoint deprecated; model/endpoint availability and optional-setting acceptance remain upstream-owned.

Only the selected actual model and method stream flag are replaced. Messages, complete reasoning/tool history, reverse tool-result order, tool IDs/argument strings, original max_tokens/max_completion_tokens, sampling/schema settings and unknown/null/false/zero/empty values remain native. The SDK does not rename deprecated parameters, clamp settings, inject tools, fetch media, assemble tool arguments or execute generated output. Passing an optional field does not establish its effect. Use explicit native JSON containers; hidden CLR containers in JsonValue are rejected before dispatch to avoid lossy Unicode serialization.

## Native failures and stream endings

HTTP failures and nonzero root `base_resp.status_code` are LlmUpstreamException with actual HTTP status, original payload bytes and content type. A native failure may have HTTP200. Read Body for arbitrary error bytes; ResponseBody uses strict UTF-8. Only native root envelopes/error events are interpreted; nested business errors stay data. There is no retry, failover or error-to-success conversion.

Native v2 may send a finish-bearing delta followed by a complete `chat.completion` message snapshot with the same finish reason. Both frames are retained, not combined or deduplicated. Every observed choice requires its own native finish reason; sparse exact indices use bounded associations. Other unfinished choices may continue after one choice finishes. Repeated chunk finishes, inconsistent identity/reason, late generation for an ended choice and repeated/reopened full snapshots fail. Native finish reasons such as length remain original outcomes, not synthetic success labels.

An actual optional `[DONE]` is consumed only after native choices end, cannot replace them, and cannot be repeated or followed by more events. No DONE is fabricated or required to replace the native v2 ending. A normal EOF alone is insufficient. From the first finish, ending-stage frames including usage/unknown metadata wait for normal source EOF, exact declared length, source/response/client/request closure and final cancellation. A late native/IO/disposal/cancellation failure prevents that successful tail from escaping. Early enumeration exit releases owned call resources.

## Local resource policy and ownership

`MiniMaxLlmOptions` defaults: MaximumRequestBytes32MiB, MaximumResponseBytes32MiB, MaximumStreamBytes32MiB, MaximumBufferedTerminalEvents128 and MaximumTrackedChoices128. All must be positive and participate in implementation identity along with the named client. Explicit/default equal policies bind identically. Request UTF-8/Unicode, raw JSON duplicates, body/media/length, reserved model/stream aliases and actual target/auth/Host/content are checked without parameter repair. Other default Key/signature headers are rejected; selected Bearer/Host/Accept override same-name defaults without mutating borrowed defaults. Reflect semantic named-client configuration changes in target Version. Disable generation POST retry/hedging in the application-owned HTTP pipeline.

Factory, Session cache and explicit binding coordinator are borrowed. Per-call requests, frozen content, returned HTTP clients, responses and body streams are owned and released. No listener, background polling, login, credential discovery, local tool loop, history storage, file manager or billing behavior is added.

## Verification

Tests verify the actual provider/public SDK with independent whole native JSON/literal SSE windows, three explicit roots, exact tool/reasoning/unknown data, native HTTP200 errors, stream-state corruption, resource limits, wire mutation, sync/async disposal/final cancellation, early exit and cross-instance Session affinity. HTTP is substituted; Session tests use MemoryDistributedCache plus an explicit test coordinator. No real MiniMax Key/model, optional-setting effect, endpoint entitlement or live generation has been tested. These checks do not establish full-repository, Linux or all-package validation.
