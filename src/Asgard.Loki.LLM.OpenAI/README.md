# Asgard.Loki.LLM.OpenAI

`OpenAiLlmProvider` remains API-Key-only. `LlmCredential.External` is an explicit core identity for providers that implement their own token/signing source; this default adapter does not opt into it. The separate [CodexLlmProvider](#explicit-codex-backend-responses) in this same package explicitly supports external subscription credentials. Direct Chat/Responses JSON/SSE, native Moderations, compaction and WebSocket operations reject both anonymous and external identities before dispatch, rather than using an empty Key.

OpenAI LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Configured native text/FIM completions | Full choices/text/logprobs JSON | Native SSE + actual DONE |
| Native Moderations | Complete native JSON; text/batch/multimodal input groups | No |
| Responses creation / continuation | Yes | SSE |
| Native Responses compaction | Yes, explicit target feature | No |
| Native Responses WebSocket | Explicit duplex connection | Native JSON events; named lanes |
| Native Realtime WebSocket | Explicit separate protocol/connection | Original JSON events; no response aggregation |

Create `OpenAiLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Configure targets with a `/v1/` base address, actual model and credential. The default provider ID is `openai`; the named HTTP client is `Asgard.Loki.LLM.OpenAI`.

Preserves extension parameters, tools, reasoning items, cache controls, original response IDs, usage, error events and unknown JSON. Session routing uses the shared LLM module. Configure project/organization defaults through the named HTTP client if needed. Disable POST retries and hedging.

## Native Realtime WebSocket

Declare `Protocols = LlmProtocol.Realtime` on the complete target. This protocol is independent of Responses and needs no Responses feature flag. The provider appends `/realtime?model={escaped actual model}` to the configured HTTP(S) root and maps HTTP(S) to WS(S), with an isolated Bearer handshake. The model is configured by the caller; there is no default model or automatic upgrade. The [official guide](https://developers.openai.com/api/docs/guides/realtime-websocket) describes this JSON protocol; GPT-Live, Responses WebSocket, WebRTC and SIP are separate interfaces.

```csharp
// Register this provider and a complete Realtime target in the existing LlmOptions.
var provider = new OpenAiLlmProvider(httpClientFactory,
    new OpenAiRealtimeOptions { MaximumMessageBytes = 16 * 1024 * 1024 });

await using var connection = await loki.LLM.Realtime.OpenAsync(
    "configured-public-model", new LlmCallOptions { SessionId = "realtime-conversation-1" }, cancellationToken);
await connection.SendAsync(new JsonObject
{
    ["type"] = "session.update",
    ["session"] = new JsonObject { ["type"] = "realtime", ["instructions"] = "Be concise." }
}, cancellationToken);
await foreach (var item in connection.ReadEventsAsync(cancellationToken))
{
    // Inspect original item.Data, including native error and response.done status.
    // The application drives input, tool results, response.create/cancel and closure.
}
```

`IOpenAiRealtimeWebSocketFactory` replaces the handshake for offline tests or application-owned transport configuration. It is borrowed; the returned open socket is owned/disposed once by the connection. The combined constructor accepts independent HTTP, Responses WebSocket and Realtime policies/factories. Default Realtime configuration preserves existing HTTP/Responses binding bytes; explicit changed limits/factory identity participate in complete-target binding. Server/model/credential/provider identity stays fixed. Known model-selection fields may repeat the public or actual model, but cannot change it; business `model` fields inside tool data are untouched.

Send snapshots precede waits. One reader and serialized writes permit duplex application code; no hidden receive loop or tool/audio state is added. The byte limit applies to complete incoming/outgoing JSON, including fragmented UTF-8. Original fields, audio base64, tool/item/call IDs, `event_id`, usage, unknown events and `response.done` statuses are returned. Metadata describes the bound target; native IDs remain in `Data` and are not registered as HTTP Responses continuation IDs.

`error` does not automatically end the connection. A `response.done` event may report completed, cancelled, incomplete or failed; it is not connection completion. Normal socket closure ends enumeration **without establishing model success** or synthesizing a terminal event; application code must inspect its expected events. Malformed envelopes, binary messages, limits, abnormal close and IO faults fail explicitly. Cancellation during IO stops uncertain transport without reconnect/replay. `CloseAsync` closes transport only; `DisposeAsync` releases it. Reopening a logical Session creates a new socket and does not restore remote history. No generation, playback, tool execution or billing loop is performed.

Independent `Fixtures/realtime.json` events cover native duplex behavior. Public SDK tests cover duplex fidelity, authentication/target affinity, original failure statuses, unknown fields, snapshots, limits, framing, cancellation and disposal. This is offline protocol verification; real OpenAI model/audio behavior is **not verified**. Ephemeral session provisioning, browser/WebRTC/SIP and cross-protocol Realtime conversion are outside this operation.

## Explicit compaction

The provider implements optional `IResponsesCompactionAdapter`. Set this on a target before using `loki.LLM.Responses.CompactAsync`:

```csharp
var target = new LlmTarget
{
    Id = "openai-compact", ProviderId = "openai",
    BaseAddress = new Uri("https://api.openai.com/v1/"),
    UpstreamModel = actualModel, CredentialId = "openai-key",
    Protocols = LlmProtocol.Responses,
    Features = LlmTargetFeatures.ResponsesCompaction
};
```

Register the target in an `LlmRoute` with the provider, caller-supplied credential, HTTP factory and existing Session dependencies. The [root example](../../README.md#native-responses-compaction) shows explicit compact/next-create calls. `ResponseCompactionRequest` preserves text/item-array input, optional owned `PreviousResponseId`, and instructions/cache/future parameters. Only the logical-to-upstream model mapping changes; there is no injected `stream`, automatic history retrieval or resubmission.

`ResponseCompaction.Data` contains the complete native result, including usage and unknown fields. `Output` exposes the full returned array, including retained messages, tool IDs and opaque encrypted compaction items. Do not extract only the encrypted item or discard retained items. An ordinary response or missing output array is rejected as an invalid compaction result. A native root error is an `LlmUpstreamException` retaining the original HTTP status (including 200) and body. Its ID is not automatically bound as a generated response for future `previous_response_id` use.

First binding filters targets by the explicit feature; an existing Session always uses its original target and fails if it lacks compaction. Feature changes invalidate existing bindings. Enabling a feature is the caller's declaration that the configured upstream/model supports it, not a live entitlement check. OpenAI-compatible third-party endpoints may not implement this native operation. There is no compaction protocol conversion, streaming, auto-trigger or retry. [Official compaction behavior](https://developers.openai.com/api/docs/guides/compaction) and [endpoint fields](https://developers.openai.com/api/reference/resources/responses/methods/compact) were checked for this implementation.

Compaction tests exercise the public SDK with substitute HTTP handlers, complete-window replay, Session ownership, failed and cancelled calls and concurrent/stale first-binding decisions. No live OpenAI compaction or account entitlement verification has been performed.

## Native Responses WebSocket

The provider implements `IResponsesWebSocketAdapter`. Set `Protocols = LlmProtocol.Responses` and `Features = LlmTargetFeatures.ResponsesWebSocket` on a native target. Enable compaction independently with the flag combination if needed. Existing Sessions keep the complete target; other providers and conversion decorators do not implicitly gain this transport. The original three-parameter HTTP constructor remains available with its previous default binding identity.

```csharp
var provider = new OpenAiLlmProvider(httpFactory); // default ClientWebSocket factory
// Optional explicit limits and borrowed custom handshake/proxy/header factory:
var customized = new OpenAiLlmProvider(httpFactory,
    new OpenAiResponsesWebSocketOptions
    {
        MaximumMessageBytes = 16 * 1024 * 1024,
        MaximumPendingRequests = 256,
        MaximumTrackedResponses = 4096,
        MaximumTrackedEventIds = 4096
    }, webSocketFactory: customFactory);
```

The target's HTTP(S) base path becomes WS(S) and ends in `/responses`; non-default ports are preserved. Each handshake uses that target's bearer credential. The named HTTP client's headers/proxy/resilience configuration is not automatically copied to `ClientWebSocket`; provide an `IOpenAiResponsesWebSocketFactory` for organization/project/custom headers or transport settings. The factory is borrowed; the returned socket belongs to the connection. Its `BindingIdentity` and non-default resource options participate in provider binding identity. Changing custom configuration invalidates old bindings. The default factory makes one handshake and disposes temporary sockets on failure; no retry or HTTP fallback occurs.

```csharp
await using var connection = await loki.LLM.Responses.OpenWebSocketAsync(
    publicModel, new LlmCallOptions { SessionId = sessionId }, cancellationToken);
await connection.SendAsync(new JsonObject
{
    ["type"] = "response.create", ["stream_id"] = "main",
    ["input"] = "Explain this code", ["store"] = false
}, cancellationToken);
await foreach (var item in connection.ReadEventsAsync(cancellationToken))
{
    ConsumeNativeEvent(item); // dispatch by JSON type/stream_id and inspect failures
    if (item.Type is "response.completed" or "response.failed" or "response.incomplete" or "error")
        break; // this example sent one request; multiplexed calls must drain every lane
}
```

Use one reader and explicit caller sends. `response.create` omits the model or supplies the public alias; the SDK sets the fixed actual model. Unknown JSON, opaque compaction/reasoning, native tool/call IDs, usage and errors stay unchanged. HTTP-only `stream`/`background` fields are rejected. Different named lanes can interleave; same-lane submissions stay associated with their own response IDs, queued/active states and optional sequence numbers. No model-specific scheduler or tool execution loop is introduced.

The connection distinguishes completed, failed and incomplete terminals, pending requests, control errors and connection-level errors. Duplicate/mismatched response identities, output after a terminal, inconsistent native status, backward sequences and closure before all generations finish fail explicitly. Unknown future events are preserved, including events with names resembling known output prefixes. An uncorrelated control error is never evidence that a generation finished. After a control whose acknowledgement cannot be proven independently, a later same-lane error without correlation IDs remains ambiguous even after the old generation ends: the raw error is returned and the new generation still requires its own terminal. Known control history is scoped to its lane; a control with unknown scope is handled conservatively. Locally unsent controls roll back that history. Native `response.steer` acknowledgement is not completion: automatic successors and explicit continuations after `response.steer.pending` are tracked; failed steering can legitimately omit its ID before acceptance. Returned required-input/tool IDs are not executed or filled by the SDK.

Limits are client memory policies, not upstream model capabilities. Messages are bounded in UTF-8 bytes, pending submissions in count, and remembered response/event identities in count. Raising them changes binding identity. An explicit client `event_id` cannot be reused on the same connection, including after its control completes: late or unknown-ID errors cannot consume a later uncorrelated generation. `MaximumTrackedEventIds` bounds that history and rejects excess new IDs before IO; a locally unsent request rolls back its reservation. On a response-identity limit error the connection aborts; the caller chooses whether and how to open another connection with suitable context. The SDK neither reconstructs history nor replays requests. Server connection/lane limits remain native errors. A socket/cache cannot restore connection-local server state; close/dispose belongs to the caller.

Offline in-memory WebSocket tests cover handshake address/authentication, public SDK Session ownership, multi-lane/FIFO transcripts, steering acceptance/rejection/automatic successors/required-input continuation, failure and truncated terminals, identity/sequence/status corruption, resource limits, byte fragmentation (including split Unicode), strict UTF-8, input snapshots, cancellation, close interruption and socket ownership. A cancelled or failed operation that has entered socket IO makes the connection unusable; queued cancellation is not replayed. No live OpenAI WebSocket, compaction or account-entitlement check has been performed. References: [WebSocket mode](https://developers.openai.com/api/docs/guides/websocket-mode), [native event schemas](https://developers.openai.com/api/reference/resources/responses/websocket-events), [steering lifecycle](https://developers.openai.com/api/docs/guides/steering). Automatic history reconstruction and durable replay are not implemented.

For OpenAI Images Generations, install the independent `Asgard.Loki.Image.OpenAI` package. This LLM provider does not pull in image or audio dependencies.

## HTTP resource policy and terminal fidelity

The existing constructor signatures remain available. Optional HTTP policy is explicit:

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory, new OpenAiHttpOptions
{
    MaximumBodyBytes = 32 * 1024 * 1024,
    MaximumBufferedTerminalEvents = 128
});
```

Both positive limits are per call. A combined overload accepts HTTP options, WebSocket options and an optional borrowed WebSocket factory. These are local resource limits, not model capability settings. The adapter preserves the explicit root prefix, actual model and caller-owned native fields; no model-name routing, endpoint fallback or cloud credential discovery is added.

Complete JSON and raw SSE bytes have strict UTF-8, duplicate-property and declared-length checks. Original HTTP/root errors retain status and body. Chat finish candidates and Responses completed/incomplete/failed/error terminals plus subsequent metadata wait for normal source EOF, content length, response/client disposal and final cancellation. `[DONE]` is consumed but does not stop reading; duplicate DONE, appended events, truncated frames and late errors fail. Incremental content before a terminal candidate remains incremental. Responses requires a native terminal with matching nonempty response ID/status; Chat requires DONE unless a native error terminates it. Failure/incomplete events remain native rather than becoming synthetic success.

Unknown fields and typed future metadata remain raw, including root business `error` fields on unknown typed events. This is a transport boundary, not a full Chat choice/tool-argument accumulator or a tool executor. Sparse choice/tool/item indices, call IDs and argument fragments are never renumbered or assembled. Endpoints must implement the declared protocol; wire preservation does not certify their model-specific reasoning/schema behavior. AWS Runtime/Mantle compatible variants remain a separate pending fixture milestone.

**Session migration:** the new `openai-http-v2` binding domain includes HTTP limits and named client. All predecessor OpenAI bindings, including WebSocket Sessions owned by this provider, require a new Session ID or expiry. Default and explicitly default HTTP options bind identically; changing policy/client invalidates an existing Session before HTTP. WebSocket framing policy and conversion-v25 algorithms are unchanged; a converter wrapping this provider incorporates its new native identity normally.

Two independently authored complete native manifests cover public SDK JSON/SSE request/response/frame snapshots, tools, reasoning/encrypted data, strict schema parameters, exact large integers and unknown fields. Fault cases cover EOF, length/byte limits, duplicate/invalid JSON, late native errors, disposal, final cancellation, early exit and cross-client target binding. No live OpenAI/model acceptance, Linux or remote Actions result is claimed.

Only explicit Responses lifecycle events register native response IDs; typed/SSE-named unknown events retain their raw nested `response` business objects without registering them as continuation targets. Empty unknown IDs no longer fail cache registration. This does not purge pre-existing cache entries or add history management. See the [core contract](../Asgard.Loki.LLM/README.md#http-responses-event-ownership).

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.

## Native Moderations

Declare `Protocols = LlmProtocol.Moderations` on an explicit complete target and call the existing LLM module's native client. No moderation model is chosen by the SDK. Register the configured model, credential, root and target separately from conversational routes.

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using System.Text.Json;

// httpClientFactory, sessionStore, apiRoot, apiKey and configuredModerationModel are caller-owned.
var provider = new OpenAiLlmProvider(httpClientFactory, new OpenAiHttpOptions
{
    MaximumBodyBytes = 32 * 1024 * 1024,
    MaximumModerationRequestBytes = 32 * 1024 * 1024
});
var target = new LlmTarget
{
    Id = "moderation-primary", ProviderId = provider.Id, BaseAddress = apiRoot,
    UpstreamModel = configuredModerationModel, CredentialId = "moderation-key",
    Protocols = LlmProtocol.Moderations
};
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("moderation-key", apiKey)],
    Routes = [new LlmRoute { Model = "classification", Targets = [target] }]
}).Build();
var result = await loki.LLM.Moderations.CreateAsync(new ModerationRequest
{
    Model = "classification",
    Parameters = new() { ["input"] = JsonSerializer.SerializeToElement(new[] { "first text", "second text" }) }
}, new LlmCallOptions { SessionId = "caller-classification-target-1" }, cancellationToken);
// Inspect original result.Data; the SDK does not make an application enforcement decision.
```

The provider appends only `moderations` under the configured HTTP(S) API root, preserving version/proxy prefixes. The public model maps to the target's actual model, while the native returned model/id remain unchanged. The required caller SessionId binds the full server/model/credential/adapter through the existing cache/coordinator; it does not store input text or implement conversational history. Classified result IDs are not registered as Responses continuation IDs. No conversion from Chat/Responses is performed.

`input` retains a scalar string, a nonempty string batch, or one nonempty array of native text/image_url parts. A text batch expects one positional result per string; a multimodal content array is one combined input and expects one result. URL and data-URL strings remain native; the SDK does not fetch media. Empty text remains empty, original whitespace/order/unknown fields are preserved and mixed/nested forms fail explicitly. No stream switch, input filter, model default, label or score is injected.

Results retain native flagged/categories/category_scores and optional category_applied_input_types, null/false fields, unknown categories/metadata and exact JSON numbers. Native id/model and complete result count are required. Known present labels are validated without filling missing ones; illicit and illicit/violent flags can be null. Known scores must be finite, without thresholding, normalization or score recalculation. Optional input-type metadata stays absent/null/native, and future label fields or input-type strings remain raw. Protocol correctness does not certify classification accuracy or opaque media interpretation.

MaximumModerationRequestBytes is optional and defaults effectively to 32 MiB; when omitted it is absent from serialized options, preserving previous default provider binding bytes. An explicit positive policy participates in the complete binding and invalidates incompatible old Sessions normally. MaximumBodyBytes bounds the entire native response/error (32 MiB by default). Both request bytes and target/model/credential are fixed before client creation. Authentication is API-Key-only; direct anonymous/external calls fail before dispatch. Unrelated default authentication headers are removed; caller-configured organization/project/other named-client headers remain available. Update the target Version if changing a named client's identity-affecting policy under the same name.

Native HTTP/HTTP200 errors retain actual StatusCode, complete Body bytes and raw ContentType through LlmUpstreamException, including binary errors or JSON errors labeled as plain text. ResponseBody is strict UTF-8 for byte errors; use Body for invalid binary text. Valid success requires complete strict UTF-8 JSON (application/json or a +json media type when supplied), unique members including unknown nested fields, compatible declared length and unchanged actual target/method. SSE is refused before reading a successful body. EOF, stream/response/client disposal and final cancellation precede result publication. No retry or target fallback is added.

The supplier has **408 offline cases**, including **91 native moderation cases**; the core has 20 additional moderation binding/protocol cases. Four independently authored full wire/result fixtures cover scalar, text batch, multimodal group and empty text. Boundary cases cover input/result association, label/null/unknown fidelity, binary/native errors, Unicode/duplicates/media/byte/length constraints, actual target/auth, resource closure/final cancellation and unchanged default binding identity. No real model/server/Key/media interpretation or classification-quality verification has been performed. Native contract references: [Moderations create](https://developers.openai.com/api/reference/resources/moderations/methods/create), [official request types](https://github.com/openai/openai-python/blob/main/src/openai/types/moderation_create_params.py) and [official result types](https://github.com/openai/openai-python/blob/main/src/openai/types/moderation.py).

## Explicit Cloudflare Workers AI profile

Use this existing supplier for account-scoped compatible Chat Completions (JSON/SSE) and supported **non-streaming** Responses Create calls. The application explicitly supplies the complete root, API token, actual model and protocol; no Cloudflare package dependency, account discovery, gateway routing or credential/model heuristic is involved.

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory, id: "workers-compatible");
var chatTarget = new LlmTarget
{
    Id = "workers-chat-account", ProviderId = provider.Id,
    BaseAddress = new Uri($"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(accountId)}/ai/v1/"),
    UpstreamModel = actualChatModel, CredentialId = "workers-token",
    Protocols = LlmProtocol.ChatCompletions
};
// Register provider, target and new LlmCredential("workers-token", apiToken)
// in the existing LlmOptions with an application-owned Session store/coordinator.
```

For Responses, explicitly configure a supported GPT-OSS model with `Protocols = LlmProtocol.Responses` and call `.LLM.Responses.CreateAsync`; current Cloudflare service support requires stream=false. This profile does not promise Responses SSE, compaction, Realtime or WebSocket support on that service. The adapter does not infer availability or select/migrate models; use independent protocol Sessions and explicit target configuration. Native `ai/run` text envelopes/SSE are a separate protocol and are not converted by this compatible profile.

Only the configured actual model and method stream switch change. Original tool IDs/arguments/results, sparse choice/tool indices, native reasoning/encrypted items, structured output settings, options.rejectIfBusy, exact numbers and unknown fields are retained. The account/root, actual model, API token identity and implementation remain Session-bound. A changed account path fails without sending to a replacement target. Factory/cache/coordinator ownership and the existing one-attempt/terminal/EOF/disposal/cancellation guarantees apply.

Fourteen independent offline profile cases bring this supplier suite to 422 passing cases. Whole wire/results/Chat event windows are checked with fake HTTP and MemoryDistributedCache, including proxy prefixes and opaque tokens. They do not verify live Cloudflare account access, actual model/setting acceptance or generation quality. See [Cloudflare compatibility and current limits](https://developers.cloudflare.com/workers-ai/configuration/open-ai-compatibility/).

## Explicit Codex backend Responses

`CodexLlmProvider` is a separate provider in this OpenAI package. It implements only existing native Responses JSON/SSE and explicit compaction interfaces with `LlmCredentialKind.External`. It reuses this package's native HTTP/Responses state and Infrastructure framing; no new capability, protocol flag, package dependency or model/agent coordinator is introduced. Chat Completions, Moderations, WebSocket/Realtime and login/account management are outside this provider.

| Operation | Support |
| --- | --- |
| Responses Create/continuation | Original JSON or SSE, complete native reasoning/tools/schema/unknown data |
| Responses compact | Original full native window under explicit ResponsesCompaction target feature |
| `CodexSearchClient.SearchAsync` | One opaque native alpha/search HTTP call; complete original body/status/media |
| Authentication | Borrowed ICodexAccessTokenSource, stable principal, fixed account ID, explicit optional Beta/originator |
| Login, auth-file loading, token refresh, subscription/account management | Not implemented |
| Live backend/account/model/parameter acceptance | Not verified; offline protocol tests only |

The endpoints are backend-specific, not a documented stable public OpenAI Platform API. Configure the complete backend/proxy API root explicitly. Official [Codex authentication](https://learn.chatgpt.com/docs/auth) distinguishes subscription credentials from Platform API keys. Official [externally managed token mode](https://learn.chatgpt.com/docs/app-server#3c-log-in-with-externally-managed-chatgpt-tokens-chatgptauthtokens) describes an experimental application-owned lifecycle; this SDK does not host that app-server/login protocol or reproduce its refresh/retry behavior.

```csharp
var provider = new CodexLlmProvider(httpClientFactory, new CodexLlmOptions
{
    Account = new CodexAccountOptions
    {
        AccountId = selectedAccountId,
        Beta = explicitBetaHeader,          // Optional; omission stays absent.
        Originator = applicationOriginator  // Optional; never guessed as a CLI.
    }
}, applicationTokenSource);
var credential = LlmCredential.External("codex-account", stablePrincipalIdentity);
var target = new LlmTarget
{
    Id = "codex-native", ProviderId = provider.Id,
    BaseAddress = new Uri(explicitBackendApiRoot), // e.g. https://host/backend-api/codex/
    UpstreamModel = configuredActualModel, CredentialId = credential.Id,
    Protocols = LlmProtocol.Responses,
    Features = LlmTargetFeatures.ResponsesCompaction
};
// Register provider/credential/target with the existing LlmOptions route and Session dependencies.
var result = await loki.LLM.Responses.CreateAsync(new ResponseRequest
{
    Model = publicModel,
    Input = JsonValue.Create("Hello"),
    Parameters = new()
    {
        ["instructions"] = JsonSerializer.SerializeToElement(explicitInstructions),
        ["store"] = JsonSerializer.SerializeToElement(false)
    }
}, new LlmCallOptions { SessionId = sessionId }, cancellationToken);
```

The application implements ICodexAccessTokenSource.GetTokenAsync for the requested stable principal, returning immutable CodexAccessToken(principal, accountId, accessToken, expiresOn). The SDK reads no environment/auth file and performs no login/discovery. A call obtains exactly one token. Wrong principal/account, expired token or changed source BindingIdentity fails without refresh or replay. Complete root/actual model/credential/principal, fixed account, optional headers, source identity, byte/tail policies, named factory and implementation participate in the existing complete-target Session binding. Tokens can rotate under the same principal/configuration; short-lived secrets stay out of fingerprints, results, JSON and default formatting.

Only selected actual model and Create/stream mode are transport-owned. Compact has no stream field. Instructions/store/reasoning/schema/tools/unsupported settings are not inserted, deleted, normalized or converted; the caller supplies settings the selected backend/model accepts and receives native errors otherwise. Entire native requests are frozen before the first token-source await. Original item/call IDs, tool output order, reasoning/encrypted/compaction items, exact values and unknown fields are preserved without execution, aggregation or decoding.

Each call uses explicit Bearer/account headers and exact application/json without charset. Omitted Beta/originator cannot be supplied by hidden named-client defaults. No account/model/root is inferred from token syntax. Source/principal/account/expiry, original method/complete URI, authentication/configuration/media and declared byte limits are checked at request/response boundaries. All owned streams/responses/clients/requests close before final binding/cancellation checks and result/held-terminal delivery. Native Responses status/response identity and unknown tails retain the existing protocol rules; EOF does not manufacture completion. HTTP and native errors retain raw bytes/status/media, including binary error bodies and late stream errors.

Defaults: 1 MiB request, 32 MiB complete/error/whole-SSE bytes and 128 held terminal-tail records. Factories, token source, clock and Session dependencies stay borrowed. Configure named transports with no automatic redirects/retries/hedging and appropriate timeouts. There is no token-triggered retry, failover, background polling or local tool/workflow/media execution.

Validation uses independent full native JSON/compact/SSE resources and fake HTTP. The complete OpenAI supplier test executable passes1213 offline cases, including127 Codex cases covering separate parallel tool IDs, native opaque fields, original wire/settings, source snapshots, complete Session drift/rotation/ownership and late transport/resource/auth failures. This is not live backend/model acceptance or a new full-repository suite result.

### Native Codex alpha search

`CodexSearchClient` is an independent fixed-service auxiliary client in the same package. Its source/credential/account checks reuse the Codex authentication implementation; bounded original-body reading uses Infrastructure. It does not create a model route, protocol flag, Session, Responses ownership or search/tool execution loop. Optional native model/stream fields stay original caller data; they do not become invented actual-model metadata.

```csharp
var search = new CodexSearchClient(httpClientFactory, new CodexSearchOptions
{
    BaseAddress = new Uri(explicitBackendApiRoot),
    Account = new CodexAccountOptions { AccountId = selectedAccountId },
    TargetId = "my-codex-search"
}, LlmCredential.External("codex-search-credential", stablePrincipalIdentity),
   applicationTokenSource);
var raw = await search.SearchAsync(nativeSearchParameters, cancellationToken);
// Interpret raw.Body explicitly according to the actual upstream content type/contract.
```

Search sends one POST to alpha/search after freezing the entire original JSON before the token-source await. It preserves omission and all unknown fields instead of adding or replacing native model, stream, instructions or user data. No search-query schema, result DTO, citations/answer extraction, usage or terminal state is inferred. The complete HTTP-success body may be JSON, text, binary, SSE or empty; Body returns an independent byte copy and ContentType preserves the original header or absence. HTTP200 is an HTTP outcome, not model completion or material validity. Non-2xx failures retain raw bytes/status/media in LlmUpstreamException without a retry or token refresh.

The fixed target/account/External principal/source and precise request headers remain checked through full bounded EOF/length/owned-resource closure and final cancellation. Defaults are 1 MiB request and 32 MiB complete response/error body. Application-owned factory/source/clock stay borrowed. Search results and URLs remain data; there is no browser/CDN downloader, webpage proxy, file manager or background workflow.

Forty offline search cases cover the independent full request/result, immutable raw body copies, optional native fields/snapshots, exact/unknown data, binary/text/JSON-error/SSE/empty windows, one-call HTTP errors, invalid configuration/Unicode/duplicates/budgets, account/principal/source/expiry failures and EOF/length/disposal/target/final-cancellation boundaries. No backend/account/search/model result has been validated live.

## Explicit Ollama compatible endpoints

Reuse this provider for compatible Chat/Responses JSON/SSE and explicit local-server compaction. Native Ollama `/api/chat` and `/api/generate` remain in [LLM.Ollama](../Asgard.Loki.LLM.Ollama/README.md). This provider sends the selected native protocol without converting Ollama NDJSON.

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory, id: "ollama-compatible",
    httpClientName: "Explicit.Ollama.Compatible");
var target = new LlmTarget
{
    Id = "my-ollama", ProviderId = provider.Id,
    BaseAddress = new Uri("http://localhost:11434/v1/"),
    UpstreamModel = selectedModel, CredentialId = "local-placeholder",
    Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Responses,
    Features = LlmTargetFeatures.ResponsesCompaction // explicitly opt in only when supported
};
// Credentials = [new LlmCredential("local-placeholder", "explicit-ignored-placeholder")]
// Register provider and target through ordinary LlmOptions with a caller-owned SessionStore.
```

The [official compatibility guide](https://docs.ollama.com/api/openai-compatibility) documents an ignored placeholder Key for local external SDK clients; local Ollama does not require authentication. This is an explicit request-local Bearer value, with no missing-Key fallback or host inference. A hosted `https://ollama.com/v1/` target instead needs its own real API Key, as documented in [authentication](https://docs.ollama.com/api/authentication). Configure a distinct complete target/credential for each service and select only its supported protocols/features. No login, model installation/discovery or server management is performed.

Responses is stateless upstream: replay complete input; do not use previous_response_id or conversation expecting remote storage. SDK Sessions keep only the existing complete-target affinity and response ownership checks. The current local server implements [responses/compact](https://github.com/ollama/ollama/blob/main/server/routes.go); hosted compact availability is not established here. Compact output remains opaque native data, including encrypted_content, without decoding, local summarization or automatic compaction. Its ID does not acquire generated Responses ownership. Native Messages uses the [existing Anthropic provider](../Asgard.Loki.LLM.Anthropic/README.md#explicit-ollama-messages); neither production supplier references the other.

Twenty-eight additional offline tests verify independently authored full native caller/wire/result/SSE windows, explicit local/proxy/hosted configuration, tools and reversed results, original reasoning/schema/unknown values, precise numbers, transport metadata, missing terminal/late read failure, raw native errors and complete Session identity drift/recovery. Total executable: 1213 passing offline cases. Configured-host wire tests do not establish hosted compact access, real model availability, optional field acceptance or generated quality. In particular, SDK field passthrough does not add upstream support for tool_choice, URL images or other unsupported settings.

## Native compatible text completions

The existing provider also implements ITextCompletionsAdapter for explicitly configured current text/FIM endpoints, such as [DashScope Completions](https://docs.modelstudio.console.alibabacloud.com/en/model-studio/completions) and [DeepSeek FIM](https://api-docs.deepseek.com/api/create-completion/). Register an ordinary complete target with `Protocols = LlmProtocol.TextCompletions`, an explicit full API root, actual model and independent API Key. The SDK appends only `completions`; a Beta prefix or FIM template belongs to the caller.

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory);
var target = new LlmTarget
{
    Id = "text-target", ProviderId = provider.Id,
    BaseAddress = new Uri(configuredTextApiRoot),
    UpstreamModel = selectedTextModel, CredentialId = "text-key",
    Protocols = LlmProtocol.TextCompletions
};
// Register provider/target, independent textKey, route "code" and caller-owned SessionStore.
var result = await loki.LLM.TextCompletions.CompleteAsync(new TextCompletionRequest
{
    Model = "code", Parameters = new()
    {
        ["prompt"] = System.Text.Json.JsonSerializer.SerializeToElement(nativePrompt),
        ["max_tokens"] = System.Text.Json.JsonSerializer.SerializeToElement(128)
    }
}, new LlmCallOptions { SessionId = "code-session" }, cancellationToken);
// TextCompletions.StreamAsync returns original TextCompletionEvent JSON and SSE metadata.
```

Requests keep original prompt/suffix/echo/logprobs, exact values, null/false/empty/unknown fields and omission. Only the selected model and method stream flag are transport-owned; aliases are rejected. The SDK neither adds FIM markers nor converts prompts to messages, clamps sampling settings, removes reasoning options or executes generated code. Native JSON containers use JsonObject/JsonArray; hidden CLR containers inside JsonValue are rejected rather than serialized with potentially changed Unicode. Complete native text choices, indices, logprobs, finish reasons and returned model stay intact. A text result ID does not acquire generated Responses ownership.

Text SSE tracks distinct exact nonnegative choice indices in a bounded dictionary; sparse values never allocate indexed arrays. A choice finish starts held-tail processing while other choices can continue. Known text/logprobs cannot reopen an ended choice; duplicate indices/ends, missing choice finishes, early/repeated/missing DONE and later data fail. Unknown business metadata remains raw and does not become a Responses lifecycle, including a response.failed-named extension. Actual DONE is a control record, not fabricated JSON. Native initial errors stay original; a late native error discards held success and retains raw bytes/status/media.

The entire request freezes before HTTP factory creation. Selected Bearer, full URI/method/Host/content and successful UTF-8 JSON/SSE media are checked through bounded length/EOF, owned async/sync/client/request closure and final cancellation. Other default key/signature auth is rejected without changing borrowed defaults. Factories, caches and binding coordinators stay caller-owned. No generated POST retries, failover, history manager, background work or media download is added.

`OpenAiHttpOptions.MaximumTextCompletionRequestBytes` is optional (effective32MiB); `MaximumTrackedTextChoices` is optional (effective128). Existing MaximumBodyBytes (32MiB) bounds JSON/errors/the entire SSE, and MaximumBufferedTerminalEvents (128) bounds ending-stage frames. All explicit policy values participate in the existing complete-target Session binding; omitted new values preserve previous default binding bytes exactly. Current protocol/model/region/parameter restrictions remain upstream-owned: DashScope documents string FIM prompts and null logprobs; generic token-array/sparse/logprobs snapshots do not certify those inputs for a particular model. No retired OpenAI model availability or live text-model acceptance is claimed.

The executable passes1213 offline cases, including140 text cases with independently authored complete JSON/raw SSE windows, original fields and method ownership, request/response faults, resource and final-cancellation failures, native choice/DONE gates and Session drift/recovery. Existing Chat/Responses/Codex/Moderations/WebSocket/Realtime cases remain included. These are protocol checks, not real model quality, setting-effectiveness or a full-repository test result.

## Explicit DashScope native endpoints

Reuse this package with the complete `compatible-mode/v1/` root, independently selected model and API Key. Native endpoint support is explicit per target:

| Native operation | SDK client | Transport |
| --- | --- | --- |
| Chat Completions | LLM.ChatCompletions | JSON/SSE, native tools/reasoning/search/schema settings |
| Responses | LLM.Responses | JSON/SSE, native reasoning/tool/search events and explicit continuation |
| Text/FIM Completions | LLM.TextCompletions | JSON/SSE, original prompt/FIM template, choices/text/logprobs |

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory,
    id: "dashscope-compatible", httpClientName: "Explicit.Ali.Native");
var chatTarget = new LlmTarget
{
    Id = "dashscope-chat", ProviderId = provider.Id,
    BaseAddress = new Uri("https://workspace-id.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/"),
    UpstreamModel = selectedChatModel, CredentialId = "dashscope-key",
    Protocols = LlmProtocol.ChatCompletions
};
// Register provider, target, independent dashscopeKey and caller-owned SessionStore.
// Declare Responses/TextCompletions on targets only for actual models supporting that protocol.
```

The SDK appends only the selected native operation path. Original legacy roots and application-selected proxy prefixes also remain explicit configuration. Region/workspace/model entitlement is caller-owned; no domain migration, model-prefix routing or model catalog is added. Native Messages uses the [existing Anthropic package](../Asgard.Loki.LLM.Anthropic/README.md#explicit-dashscope-messages), with no supplier-peer reference.

Original top_p, thinking_budget/enable_thinking, response_format, reasoning/text settings, explicit tools and future fields are not clamped, removed, defaulted or inferred from model names. FIM markers remain caller text. Native optional headers belong to the application-owned named HTTP client; reflect semantic header changes in LlmTarget.Version. Tool/plugin/search results, usage details and references stay remote data, without local execution, media retrieval or billing logic. Parameter preservation does not establish that the selected model accepts every combination.

Responses known reasoning_text, custom_tool_call_input and web_search_call lifecycle events cannot reopen generation after a native terminal. They remain original JSON before terminal. Unknown response metadata stays raw. Chat extension type values and business IDs without choices remain data: a response.failed-named extension does not become a Responses failure, while actual native error events keep their error semantics. These corrections retain existing default identity serialization and native protocol boundaries.

Thirty-five complete-profile cases verify legacy/workspace/proxy roots, JSON/SSE over all three native operations, opaque reasoning/schema/tool histories, reverse tool results, exact unknown numbers, transport metadata and Session identity/continuation. The supplier executable passes1213 offline cases. No DashScope account/model/optional-setting/plugin/search quality has been validated live. Current upstream contracts: [Chat](https://www.alibabacloud.com/help/en/model-studio/qwen-api-via-openai-chat-completions), [Responses](https://help.aliyun.com/zh/model-studio/qwen-api-via-openai-responses), [Completions](https://docs.modelstudio.console.alibabacloud.com/en/model-studio/completions).

## Explicit DeepSeek native endpoints

Configure separate complete targets with independently selected actual models and API Keys. The provider appends the operation path only; it does not infer a root or rewrite model suffixes. The existing [Anthropic package](../Asgard.Loki.LLM.Anthropic/README.md#explicit-deepseek-messages) owns native Messages separately.

| Native operation | Complete SDK BaseAddress | Protocol / SDK client |
| --- | --- | --- |
| Chat | `https://api.deepseek.com/v1/` | ChatCompletions / LLM.ChatCompletions |
| Responses | `https://api.deepseek.com/` | Responses / LLM.Responses |
| Beta text/FIM | `https://api.deepseek.com/beta/` | TextCompletions / LLM.TextCompletions |

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory,
    id: "deepseek-native", httpClientName: "Explicit.DeepSeek.Native");
var target = new LlmTarget
{
    Id = "deepseek-responses", ProviderId = provider.Id,
    BaseAddress = new Uri("https://api.deepseek.com/"),
    UpstreamModel = selectedResponsesModel, CredentialId = "deepseek-key",
    Protocols = LlmProtocol.Responses
};
// Register provider/target, independent deepSeekKey and caller-owned SessionStore in LlmOptions.
// Register separate selected Chat or beta TextCompletions targets with their complete roots.
```

[Responses](https://api-docs.deepseek.com/guides/responses_api/) is stateless: send complete input on every call, including original reasoning and tool call/output history. The SDK does not inject previous_response_id, conversation state or hidden history. Native reasoning_text, custom apply_patch tool input/output, call IDs, schema/effort, usage and future fields remain data. No local patch/search/tool execution occurs. Native completed/incomplete/failed events retain their status; SSE requires its semantic terminal without manufacturing `[DONE]`. Core ownership of returned IDs does not establish that this endpoint supports server-side continuation.

[Chat](https://api-docs.deepseek.com/api/create-chat-completion/) preserves reasoning_content, selected thinking/effort, JSON mode, original tool IDs and argument fragments. [Beta FIM](https://api-docs.deepseek.com/api/create-completion/) preserves caller prompt/suffix, logprobs or echo: the service forbids combining echo with suffix or logprobs. For streaming include_usage, usage accompanies the final content choice; no separate usage-only frame is required. Actual `[DONE]` remains required. The SDK does not insert FIM markers, normalize effort, strip model suffixes or repair unsupported parameter combinations.

Thirty-seven independently authored whole-window/fault cases verify native and proxy roots, full JSON/SSE for Chat/Responses and three mutually valid FIM variants, exact unknown values, reversed tool outputs, original metadata, terminal loss, stateless Session recovery and root/key drift. Current supplier total:1213 passing offline cases. No real DeepSeek account/model, optional-setting effectiveness or beta availability was tested. Protocol/model/parameter support is caller-declared per target; preserving optional fields does not certify acceptance.

## Explicit Moonshot and Kimi Coding native endpoints

Reuse this provider with a separately selected complete root, actual model and independent API Key. The provider appends `chat/completions` or `responses`; it does not select a region, infer a Coding plan, clamp temperature or map thinking/effort from model names.

| Service | Complete SDK BaseAddress | Explicit target protocols |
| --- | --- | --- |
| Moonshot China | `https://api.moonshot.cn/v1/` | ChatCompletions or Responses |
| Moonshot international | `https://api.moonshot.ai/v1/` | ChatCompletions or Responses |
| Kimi Coding China | `https://api.kimi.com/coding/v1/` | ChatCompletions or Responses |
| Kimi Coding international | `https://api.kimi.ai/coding/v1/` | ChatCompletions or Responses |

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory,
    id: "moonshot-native", httpClientName: "Explicit.Moonshot.Native");
var target = new LlmTarget
{
    Id = "moonshot-responses", ProviderId = provider.Id,
    BaseAddress = new Uri("https://api.moonshot.ai/v1/"),
    UpstreamModel = selectedResponsesModel, CredentialId = "selected-platform-key",
    Protocols = LlmProtocol.Responses
};
// Register provider/target, the independent platform Key and caller-owned SessionStore.
// A Coding target uses its own configured root, model and credential identity.
```

[Chat](https://platform.kimi.ai/docs/api/chat) retains reasoning_content history, thinking.type/keep, tool calls/results and their order, native schema/effort/cache settings, unknown fields and original counters. Streaming usage may accompany the last content/finish chunk; actual `[DONE]` is required. [Partial Mode](https://platform.kimi.ai/docs/api/partial) remains a final assistant message containing `partial: true` and the exact prefilled content. It is not a root parameter or a text-completion conversion; the application decides how to combine returned text with its prefix. Use a separate request from JSON mode as required by the native service.

[Responses](https://platform.kimi.ai/docs/api/responses) receives complete ordered input on each call; the SDK adds no previous_response_id or conversation history. Original reasoning summary parts, optional input reasoning content, null encrypted_content, custom apply_patch and function item/call IDs, schema/effort, cache read/write details and future fields stay intact. No encrypted reasoning, signature, usage total or tool result is invented. Native completed/incomplete/failed outcomes retain their original status and details; their SSE semantic terminal does not require a fabricated DONE.

Known response.reasoning_summary_part.added/done and response.reasoning_summary_text.delta/done are native generation events: they are returned before terminal and rejected after terminal. Unknown similarly named metadata remains original data; it cannot acquire response ownership through a nested business ID. Existing bounded ending-stage EOF, length, resource disposal and final-cancellation gates remain unchanged.

Native [Messages](../Asgard.Loki.LLM.Anthropic/README.md#explicit-moonshot-and-kimi-coding-messages) uses the existing separate package. No CLI configuration, client impersonation, model discovery, automatic tool/patch execution, retry or gateway orchestration is performed. [Coding service access](https://www.kimi.com/code/docs/en/) and optional model/setting support remain upstream-owned and caller-selected. This package's native TextCompletions client supports explicitly configured compatible services; no current hosted Moonshot legacy `/v1/completions` availability is claimed. Partial Mode is the separately documented Chat operation above.

Fifty-two independent-profile/fault cases verify all configured roots, full Chat/Partial/Responses JSON/SSE, exact unknown values, original tool/reasoning/schema/cache data, missing and late native endings, stateless Session recovery and complete root/key/model/client/policy affinity. Current supplier total:1213 passing offline cases. No real account/model, Coding entitlement, optional-setting effect or hosted legacy text operation has been tested.

## Explicit Mistral native Chat

Reuse this provider with an explicit complete `https://api.mistral.ai/v1/` root, selected actual model and independent API Key. Configure only ChatCompletions for this target; this profile does not declare other protocols. Application-selected proxy prefixes remain explicit configuration. The provider appends `chat/completions` and keeps the selected Bearer credential.

```csharp
var provider = new OpenAiLlmProvider(httpClientFactory,
    id: "mistral-native", httpClientName: "Explicit.Mistral.Native");
var target = new LlmTarget
{
    Id = "mistral-chat", ProviderId = provider.Id,
    BaseAddress = new Uri("https://api.mistral.ai/v1/"),
    UpstreamModel = selectedMistralModel, CredentialId = "mistral-key",
    Protocols = LlmProtocol.ChatCompletions
};
// Register provider/target, independent mistralKey and caller-owned SessionStore in LlmOptions.
// Use LLM.ChatCompletions.CompleteAsync / StreamAsync with the public route model.
```

The [native Chat contract](https://docs.mistral.ai/api/endpoint/chat) preserves full messages, tool names/IDs, object or string function arguments, reversed tool results, original image_url string/object containers, sampling settings, seed, cache key, schema/JSON mode, prefix and unknown fields. Only selected actual model and method stream flag are owned. No tool ID remapping, media retrieval, request-field selection, token-parameter renaming, setting clamping or model-name inference occurs. Nonstandard IDs/unsupported fields remain caller data and may be rejected upstream; forwarding is not acceptance or effectiveness verification.

[Native reasoning](https://docs.mistral.ai/studio/conversations/reasoning) stays in original content: thinking chunks contain their own text-part arrays, closed flag and optional original signature; text chunks remain separate. During SSE, content can change from a thinking array to a mixed thinking/text array and then a plain text string. The SDK neither flattens this into reasoning_content nor assembles or deduplicates it. Use the complete returned native content when replaying history. Assistant prefix:true remains a message field; JSON object/schema modes use their own native settings and model restrictions.

Actual `[DONE]` is required. Finish-bearing chunks and subsequent metadata wait through normal source EOF, owned resource closure and final cancellation; missing/repeated DONE, appended events and late IO failures cannot release successful endings. Native finish_reason values such as length or error remain original outcomes for the caller to inspect, without synthetic stop/completed labels. Unknown typed business metadata and business IDs remain raw. HTTP errors retain their original status/body; there is no automatic retry, target switch, local tool executor or history manager.

Forty-four independent-window/fault cases verify native/proxy roots, complete JSON/SSE for reasoning/tools/prefix/JSON modes, original caller snapshots, mixed content types, exact large integers, unknown values, tool links, native outcomes, HTTP errors, late/truncated ends, early exit and root/key/model/client/policy/version Session affinity/recovery. Current supplier total:1213 passing offline cases. No production package/API/dependency change is required for these profiles. Real Mistral account/model, optional-setting support/effectiveness and live tool/media behavior remain unverified. The [official OpenAPI](https://docs.mistral.ai/openapi.yaml) describes native message and content fields; SDK event wrapper objects are not added as a new JSON envelope to SSE payloads.

## Explicit xAI native Chat and Responses

Use this provider with an explicit complete `https://api.x.ai/v1/` root, selected actual model and independent API Key. Proxy prefixes remain explicit configuration. Native Chat appends `chat/completions`; native Responses appends `responses`. There is no hostname inference or automatic protocol migration.

| Native operation | Data and limits |
| --- | --- |
| Chat JSON/SSE | Full messages/image references, original tool IDs/reversed results/argument strings, reasoning_content, citations and original usage |
| Responses JSON/SSE | Full input/output items, opaque encrypted reasoning/tool history, original call_id/item_id, annotations, native status and usage |
| Native settings | Chat response_format and reasoning_effort; Responses text.format and reasoning/alternate reasoning_effort stay in their original locations |
| Server search/tool data | Original request definitions and returned search/usage/encrypted data; no local tool loop, execution or result download |
| Verification | Offline protocol tests only; actual model availability, settings and encrypted-content acceptance remain unverified |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedXaiModel and selectedXaiKey are caller-owned.
var provider = new OpenAiLlmProvider(factory,
    id: "xai-native", httpClientName: "Explicit.XAI.Native");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("xai-key", selectedXaiKey)],
    Routes = [new LlmRoute
    {
        Model = "xai-responses", Targets = [new LlmTarget
        {
            Id = "xai-selected", ProviderId = provider.Id,
            BaseAddress = new Uri("https://api.x.ai/v1/"),
            UpstreamModel = selectedXaiModel, CredentialId = "xai-key",
            Protocols = LlmProtocol.Responses
        }]
    }]
}).Build();
var response = await loki.LLM.Responses.CreateAsync(new ResponseRequest
{
    Model = "xai-responses", Input = JsonValue.Create("Hello")
}, new LlmCallOptions { SessionId = "xai-responses-1" }, cancellationToken);
// response.Data is complete native JSON; Responses.StreamAsync returns original events.
```

For native Chat, declare a ChatCompletions target and call `LLM.ChatCompletions.CompleteAsync` / `StreamAsync` with ChatCompletionRequest. The [native Chat reference](https://docs.x.ai/developers/rest-api-reference/inference/chat-completions) still documents this legacy operation; [Responses](https://docs.x.ai/developers/rest-api-reference/inference/responses) is the upstream's primary text interface. Choose actual models and settings explicitly. The SDK does not strip search/high/low suffixes, inject search_parameters, rename max_tokens or repair unsupported reasoning parameters. Native search settings are forwarded unchanged; a deprecated/unsupported model or field may be rejected upstream without a retry or alternate target.

Both nested reasoning and the native flat reasoning_effort alternative are forwarded as supplied, including both when explicitly present; their precedence and effect belong to the upstream. Chat response_format and Responses text.format are not translated on native calls. Native prompt_cache_key remains a request field, with no synthesized headers. There is no automatic store/include default, response retrieval, hidden conversation history or compaction. Use explicit store:true and previous_response_id when selecting stored upstream continuation; replay complete original input/output items when managing stateless history. Encrypted reasoning and server-tool outputs remain opaque JSON and are never decrypted, reconstructed or discarded. Media URLs remain remote references.

All original usage fields remain native, including context_details, server_side_tool_usage_details, cost ticks, exact large integers and token counts. The SDK does not recompute totals/text/reasoning tokens or calculate billing. Forwarding or replaying data is not proof that the upstream accepted it or applied a parameter.

Native first SSE error/response.failed frames are returned as original protocol events with their complete payloads; consumers inspect their native type/status/error. They are not rewritten to completed. Native response.incomplete retains its own status/details. An error after a possible terminal raises LlmUpstreamException with the original error payload and discards the buffered ending. Full HTTP/JSON errors retain the existing LlmUpstreamException/status/UTF-8 ResponseBody contract.

Chat requires an actual DONE. Responses requires its native completed/incomplete/failed/error terminal; an optional actual DONE follows it and cannot substitute for it. Native known reasoning/function/search/image-generation events cannot reopen a terminal response. The three exact image_generation_call in_progress/generating/completed event names are native lifecycle data; similar business-event prefixes remain raw unknown metadata. No tool/image operation is executed by the SDK. Endings and following metadata wait through actual EOF, owned-resource closure and final cancellation; late failures discard the ending. Local OpenAiHttpOptions byte/tail limits and named-client identity apply unchanged. Cache, explicit binding coordinator and factory are borrowed; returned call resources are owned. Reflect semantic named-client configuration changes in target Version and disable generation retry/hedging in the caller-owned pipeline.

Independent native/proxy JSON and literal-SSE windows verify the public SDK, complete caller/wire/result/events, encrypted replay, reverse tool associations, exact native settings/usage, raw errors, incomplete/failed outcomes, known post-terminal generation, unknown metadata, late read/final cancellation, early exit and complete Session affinity. No real xAI Key/model, parameter effect, native search execution or ciphertext validity has been tested.

## Explicit SiliconFlow native Chat, Prefix and FIM

Reuse this provider with an explicit complete `https://api.siliconflow.cn/v1/` root, independently selected API Key and actual upstream model. Declare a ChatCompletions-only target. Proxy roots and model names remain caller-owned; no region, model suffix or alternative protocol is inferred.

| Native mode | Support |
| --- | --- |
| Chat JSON/SSE | Full messages, reasoning_content, original usage/cache details, exact unknown values and transport event metadata |
| Function tools | Original call IDs, argument strings/fragments, reversed results and returned tool order; no execution or argument assembly |
| Structured output | Original response_format json_schema/json_object, caller messages and native finish reasons; no JSON repair |
| Multimodal input | Original text/image_url/audio_url/video_url content parts and native URL/detail/frame parameters; no media fetching |
| Prefix completion | Root prefix with caller-supplied messages; original returned text |
| FIM completion | Root prefix and suffix through ChatCompletions, with caller-supplied messages; no synthetic empty user message or combined code output |
| Other native protocols | This target declares Chat only; no Responses, Messages or text-completions endpoint is inferred |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using System.Text.Json;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedSiliconFlowModel and selectedSiliconFlowKey are caller-owned.
var provider = new OpenAiLlmProvider(factory,
    id: "siliconflow-native", httpClientName: "Explicit.SiliconFlow.Native");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("sf-key", selectedSiliconFlowKey)],
    Routes = [new LlmRoute
    {
        Model = "sf-chat", Targets = [new LlmTarget
        {
            Id = "sf-selected", ProviderId = provider.Id,
            BaseAddress = new Uri("https://api.siliconflow.cn/v1/"),
            UpstreamModel = selectedSiliconFlowModel, CredentialId = "sf-key",
            Protocols = LlmProtocol.ChatCompletions
        }]
    }]
}).Build();
var result = await loki.LLM.ChatCompletions.CompleteAsync(new ChatCompletionRequest
{
    Model = "sf-chat",
    Messages = new JsonArray(new JsonObject
    {
        ["role"] = "user", ["content"] = "Fill only the middle of the supplied code."
    }),
    Parameters = new()
    {
        ["prefix"] = JsonSerializer.SerializeToElement("def sort_values(values):\n"),
        ["suffix"] = JsonSerializer.SerializeToElement("\nprint(sort_values([2, 1]))\n")
    }
}, new LlmCallOptions { SessionId = "sf-fim-1" }, cancellationToken);
// result.Data is complete native JSON. StreamAsync returns original deltas and metadata.
```

The [native Chat API](https://docs.siliconflow.cn/docs/api/chat-completions-post), [Prefix guide](https://docs.siliconflow.cn/docs/userguide/guides/prefix) and [FIM guide](https://docs.siliconflow.cn/docs/userguide/guides/fim) use `chat/completions`. Prefix-only calls omit suffix; ordinary Chat omits both. The Python documentation's extra_body merges these properties into the wire root; do not wrap them in an extra_body JSON object here. FIM is a native Chat parameter mode and does not call `/completions` or assemble prefix + generated content + suffix locally. Supply the actual required messages yourself. Empty/rejected inputs remain original and are never repaired by adding a blank user message.

The SDK replaces only the selected actual model and method stream flag. enable_thinking, thinking_budget, reasoning_effort, min_p, max_tokens, schema settings, content-part containers and unknown/null/false/zero/empty values remain exactly caller-supplied. It does not flip thinking flags, clamp or map effort, rename token fields, delete tools, flatten reasoning or infer model capabilities. Settings and modality support vary by actual model and upstream policy; forwarding a field does not establish acceptance or effect. Native response text, tool IDs/fragments and usage/cache fields remain original, including a length outcome with incomplete JSON text.

SSE requires the upstream's actual `[DONE]`. Finish-bearing and following usage/unknown metadata frames wait for normal source EOF, owned-resource closure and final cancellation. Missing/repeated/post-DONE data, inconsistent native response identity and late IO/cancellation fail without delivering buffered endings. Unknown business metadata is not treated as Responses state. Early exit releases only the current call, without background consumption, polling or retry. Numeric native code/message/data HTTP errors preserve actual status and original UTF-8 ResponseBody through the existing LlmUpstreamException contract; HTTP429/503 do not trigger another SDK request.

Existing OpenAiHttpOptions byte/tail limits and complete root/model/credential/provider/client/policy/version Session identity apply unchanged. Cache, explicit binding coordinator and HTTP factory are borrowed; per-call clients/messages/responses/streams are owned. Reflect semantic named-client configuration changes in target Version and disable generation retries/hedging in the application-owned HTTP pipeline. Independent native/proxy complete JSON and literal-SSE windows verify seven native modes through the public SDK, original callers and error/resource/Session boundaries. No real SiliconFlow Key/model, modality generation/understanding, setting effect or FIM output quality has been tested.

## Explicit Baidu Qianfan v2 native Chat

Use OpenAiLlmProvider with an explicitly selected complete v2 root, actual model, independent native API Key and ChatCompletions-only target. Optional appid is an application-owned named HttpClient default header. The Key remains opaque: the SDK does not split token|appid strings or infer search behavior from a model suffix.

| Operation | Support | Limits / live status |
| --- | --- | --- |
| Native Chat JSON / SSE | Complete original request, result and events | Offline HTTP substitutes only; no real account/model validation |
| Reasoning / tools / JSON schema | Original reasoning_content, tool IDs/history/fragments and native response_format | Caller selects accepted model/settings; no tool execution or schema repair |
| Native web_search | Explicit root fields, choice-level delta_tag, root search_results and original citations | No injected search, local retrieval or UI interpretation |
| Filtering / usage | Original flag, ban_round, empty reported model and exact usage extensions | No local filtering, policy management or usage arithmetic |
| Other protocols | Target declares only native Chat | No endpoint or capability inferred from the hostname |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

// services, sessionStore, selectedApiKey, selectedModel and selectedAppId are caller-owned.
services.AddHttpClient("Explicit.BaiduV2.App", client =>
    client.DefaultRequestHeaders.Add("appid", selectedAppId)); // Omit for a no-appid profile.
using var serviceProvider = services.BuildServiceProvider();
var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
var provider = new OpenAiLlmProvider(factory,
    id: "baidu-v2-native", httpClientName: "Explicit.BaiduV2.App");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("baidu-key", selectedApiKey)],
    Routes = [new LlmRoute
    {
        Model = "baidu-chat", Targets = [new LlmTarget
        {
            Id = "baidu-selected", ProviderId = provider.Id,
            BaseAddress = new Uri("https://qianfan.baidubce.com/v2/"),
            UpstreamModel = selectedModel, CredentialId = "baidu-key",
            Protocols = LlmProtocol.ChatCompletions, Version = "app-context-v1"
        }]
    }]
}).Build();
var result = await loki.LLM.ChatCompletions.CompleteAsync(new ChatCompletionRequest
{
    Model = "baidu-chat",
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" })
}, new LlmCallOptions { SessionId = "baidu-session-1" }, cancellationToken);
// result.Data is complete native JSON; StreamAsync retains original deltas and metadata.
```

The [native REST reference](https://cloud.baidu.com/doc/qianfan-api/s/3m7of64lb) and [SDK guide](https://cloud.baidu.com/doc/qianfan-docs/s/Fm9l6ocai) specify Bearer authentication, optional default_headers appid and native SSE ending in actual `[DONE]`. Python extra_body merges into the JSON root; pass optional web_search, safety, thinking, enable_thinking, reasoning_effort and token limits as original request Parameters. Neither max_tokens/max_completion_tokens nor conflicting settings are renamed, inferred or repaired. Native json_schema.schema, tool association IDs, reversed results, search status deltas and filtering metadata remain complete. The actual model may reject settings; field forwarding does not prove their effect.

Finish-bearing frames and following usage/unknown metadata wait for actual DONE, normal EOF, owned-resource closure and final cancellation. Missing/repeated/post-DONE data, native identity drift and late IO/cancellation fail without releasing buffered endings. Native content_filter/length reasons and incomplete JSON text remain original. HTTP400/401/429/503 keep actual status and original UTF-8 code/message/type ResponseBody in LlmUpstreamException; no automatic retry, target switch or credential acquisition occurs.

Complete root/model/credential/provider/client/policy/version Session identity and existing byte/tail limits apply. A named-client pipeline is trusted caller configuration: when appid or other semantic defaults change, change the client name or target Version before reusing Sessions. The SDK does not hash arbitrary factory internals. Cache, explicit coordinator and factory are borrowed; per-call HTTP resources are owned. Disable generation retries/hedging in the caller pipeline. Independent plain/appid/proxy JSON/SSE windows and fault tests verify these boundaries offline. No real Key, model, search/filter policy, parameter effect or generated quality has been validated.

## Explicit Zhipu v4 native Chat and Responses

Reuse OpenAiLlmProvider with complete caller-selected targets and independent native API Keys. Chat and Responses have separate roots and native schemas. The SDK does not choose a Coding Plan or infer a protocol from a model name.

| Operation | Complete root / declared protocol | Native boundaries |
| --- | --- | --- |
| Chat JSON / SSE | `https://open.bigmodel.cn/api/paas/v4/` / ChatCompletions | Native thinking/reasoning_content, tools, web_search, content_filter and original usage; actual `[DONE]` required |
| Responses JSON / SSE | `https://open.bigmodel.cn/api/v1/` / Responses | Original input/output, reasoning.content object, function IDs, JSON-object format, search and usage; native response terminal, no DONE required |
| Messages JSON / SSE | Separate [Anthropic package](../Asgard.Loki.LLM.Anthropic/README.md#explicit-zhipu-v4-native-messages) | Explicit native Messages target and authentication; no supplier-peer dependency |
| Other operations | No capability inferred | No provider provisioning, saved-response administration, model catalog or background workflows |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedResponseModel and selectedResponseKey are caller-owned.
var provider = new OpenAiLlmProvider(factory,
    id: "zhipu-native", httpClientName: "Explicit.Zhipu.Responses");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("zhipu-key", selectedResponseKey)],
    Routes = [new LlmRoute
    {
        Model = "zhipu-response", Targets = [new LlmTarget
        {
            Id = "zhipu-response-selected", ProviderId = provider.Id,
            BaseAddress = new Uri("https://open.bigmodel.cn/api/v1/"),
            UpstreamModel = selectedResponseModel, CredentialId = "zhipu-key",
            Protocols = LlmProtocol.Responses
        }]
    }]
}).Build();
var result = await loki.LLM.Responses.CreateAsync(new ResponseRequest
{
    Model = "zhipu-response", Input = JsonValue.Create("Hello")!
}, new LlmCallOptions { SessionId = "zhipu-response-1" }, cancellationToken);
// For Chat, explicitly configure the Chat root/protocol and call ChatCompletions.
```

The [native Chat REST reference](https://docs.bigmodel.cn/api-reference/模型-api/对话补全), [Responses guide](https://docs.bigmodel.cn/cn/guide/develop/responses/introduction) and [Responses REST reference](https://docs.bigmodel.cn/api-reference/response/创建-response) define the actual schemas. Pass original native settings via Parameters: thinking.clear_thinking, reasoning_effort, top_p, stop, tool definitions, format fields and request_id are not rewritten. Native Chat response_format and Responses text.format have different positions and currently document text/json_object; field passthrough does not certify unsupported schema formats. top_p=1 is not clamped, data URIs are not stripped, token limits are not merged and native history is not filtered. Model-specific validation and effort mapping belong to the selected upstream.

Responses store defaults to false upstream; the SDK injects no store/continuation values and retains explicit false or caller previous_response_id. Native reasoning.content may be an object, and usage may omit a total; no normalization or arithmetic is applied. Function call IDs, reversed results and raw fragments remain original. Search and filtering metadata stay data; no local search, safety policy, tool execution or media download is performed.

Existing raw JSON errors use LlmUpstreamException with actual HTTP status and original UTF-8 ResponseBody, including HTTP200 failed JSON with a native error object. Native SSE failed/incomplete events retain their actual snapshots and partial outputs; they do not become completed. Missing/repeated terminal, known generation after terminal, late source IO/resource cancellation and early consumer exit follow existing EOF/closure gates and ownership. Complete target/credential/client/policy/version Session binding and byte/tail limits remain unchanged; borrowed cache, coordinator and factory stay caller-owned. Disable retries/hedging in the application HTTP pipeline. Independent native/proxy whole windows and fault cases are offline protocol checks only; no real Key/model, Coding Plan entitlement, setting/search/filter effect or generated quality has been tested.

## Explicit OpenRouter, Xinference and Submodel profiles

Use the existing OpenAiLlmProvider for explicitly selected compatible native operations. Configure the complete root, accepted actual model, independent Key, declared protocol and named client yourself. A provider class exposing an operation does not establish that a particular service/model supports it.

| Service / operation | Complete root example / protocol | Support and limits |
| --- | --- | --- |
| OpenRouter Chat JSON / SSE | `https://openrouter.ai/api/v1/` / ChatCompletions | Original reasoning_details, opaque encrypted/signature items, tools, native_finish_reason and exact usage; optional attribution headers are caller-owned |
| OpenRouter Responses JSON / SSE | Same root / Responses | Stateless native input/output and response terminal; explicit store/previous_response_id are not rewritten |
| Configured OpenRouter legacy text JSON / SSE | Same root / TextCompletions | Explicit `/completions` wire profile, separate from prompt input at the Chat endpoint; actual legacy model/endpoint acceptance is unverified |
| Xinference Chat JSON / SSE | Caller-deployed complete `/v1/` root / ChatCompletions | Explicit model UID, root enable_thinking and original tools/settings; no server/model launch or capability detection |
| Xinference text JSON / SSE | Same selected server root / TextCompletions | Original prompt/suffix/stop/echo/logprobs and raw choices; no conversion to messages |
| Configured Submodel Chat / text JSON / SSE | Historical `https://llm.submodel.ai/v1/` or caller-selected complete proxy root / corresponding protocol | Original compatible wire; no automatic migration to newer branded domains or serverless instance routes; hosted legacy availability is unverified |
| Other native protocols | No inference from service name or generic adapter interfaces | No native Submodel Responses/Messages/Gemini capability claim; no control-plane operations |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using System.Text.Json;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedOpenRouterModel and selectedOpenRouterKey are caller-owned.
var provider = new OpenAiLlmProvider(factory,
    id: "openrouter-native", httpClientName: "Explicit.OpenRouter.Native");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("or-key", selectedOpenRouterKey)],
    Routes = [new LlmRoute
    {
        Model = "or-chat", Targets = [new LlmTarget
        {
            Id = "or-selected", ProviderId = provider.Id,
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            UpstreamModel = selectedOpenRouterModel, CredentialId = "or-key",
            Protocols = LlmProtocol.ChatCompletions, Version = "caller-context-v1"
        }]
    }]
}).Build();
var result = await loki.LLM.ChatCompletions.CompleteAsync(new ChatCompletionRequest
{
    Model = "or-chat",
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" }),
    Parameters = new()
    {
        ["reasoning"] = JsonSerializer.SerializeToElement(new
        {
            enabled = true, effort = "medium", exclude = false
        })
    }
}, new LlmCallOptions { SessionId = "or-session-1" }, cancellationToken);
```

For Responses or text, initialize a separate explicit target using the matching table protocol and call Responses.CreateAsync/StreamAsync or TextCompletions.CompleteAsync/StreamAsync. TextCompletionRequest.Parameters contains native prompt and optional text settings; the selected method owns stream, so do not place stream in that extension dictionary. Raw JSON results and events remain native; no text-to-Chat conversion is performed. Xinference requires the caller's actual deployed root/model UID and accepted authentication Key. The SDK does not supply placeholder credentials, start a server, inspect environments or obtain credentials.

OpenRouter's [native Chat stream contract](https://openrouter.ai/docs/api_reference/streaming) repeats finish_reason/native_finish_reason on the final usage-bearing choice before actual `[DONE]`. Both original frames are retained; this usage frame is not treated as a duplicate control terminal. Actual DONE duplication, data after DONE, missing native control, Responses terminal repetition and known generation after a Responses terminal fail. Native error chunks, including an error as the first/only Chat event without DONE, remain original failures; a late error after a possible normal ending throws with its original body and withdraws buffered endings. Normal endings wait for source EOF, owned-resource closure and final cancellation. Early exit closes the current call only.

The [Responses API](https://openrouter.ai/docs/api_reference/responses/overview) is stateless and rejects store=true/non-null previous_response_id upstream. The SDK preserves explicit values without injecting defaults. Existing Session affinity rejects an unknown previous ID before IO; a known bound ID is sent unchanged and may still be rejected by this upstream. Supply full original history yourself. Native reasoning_details/encrypted content, separate tool IDs, reversed results/fragments, unknown/null/false/zero/empty fields and inconsistent raw totals are not decoded, aggregated or repaired. Optional HTTP-Referer/X-OpenRouter-Title are configured only by the caller's named HTTP client. No model-suffix effort rewrite, default attribution or usage.include injection is performed. When header semantics change, change client name or target Version; arbitrary factory internals are trusted configuration, not automatically hashed.

[Xinference Chat/Generate](https://inference.readthedocs.io/en/latest/models/model_abilities/chat.html) documents distinct native Chat and text endpoints; Python extra_body merges into the root. [Submodel's published serverless reference](https://api.submodel.ai/doc/) documents instance-specific Chat/text routes; configure a complete supported route prefix explicitly and do not substitute it for a historical target silently. The [OpenRouter announcement](https://openrouter.ai/blog/announcements/response-caching/) still names legacy completions separately; no legacy acceptance, caching or hosted model effect has been tested here. Current branded model listings and unrelated administration APIs do not widen these SDK protocol profiles.

Existing byte/tail/choice limits, raw HTTP error status/UTF-8 ResponseBody, full root/model/credential/provider/client/policy/version Session binding and borrowed cache/coordinator/factory ownership apply unchanged. Disable generation retries/hedging in the caller HTTP pipeline. Independent seven-window native/proxy JSON/SSE fixtures and 99 additional cases verify caller immutability, native error handling, EOF/cancellation/early exit, complete binding drift/replay and explicit optional headers offline. No real service/model/legacy endpoint, setting effect, encrypted item acceptance or generated quality has been validated. This adds no supplier package, public API, retry/fallback coordinator, polling, model catalog or administrative/billing feature.
