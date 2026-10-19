# Asgard.Loki.LLM.Anthropic

Native Claude Messages provider for the Asgard.Loki .NET SDK.

| API / feature | Support |
| --- | --- |
| `POST /v1/messages` | Complete JSON and SSE |
| Tools, image/document blocks, thinking/signatures, cache controls, future JSON fields | Preserved in their native shape |
| Unknown SSE events, event IDs and retry hints | Preserved; hints do not cause retries |
| HTTP errors / interrupted streams | Original HTTP status/body; missing `message_stop` or error terminal fails |
| Bedrock Runtime/Mantle native Messages, explicit Key | Offline native HTTP/JSON/SSE fixtures through this provider |
| Explicit Bearer header | Caller-selected static credential; endpoint acceptance remains caller-owned |
| Cloud SigV4/token refresh, token-count and batch APIs | Not implemented here; use owning cloud provider where supported |

```csharp
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Anthropic;

var provider = new AnthropicLlmProvider(httpFactory);
// Register provider in LlmOptions.Providers and configure an ordinary LlmTarget:
var target = new LlmTarget
{
    Id = "claude-primary", ProviderId = provider.Id,
    BaseAddress = new Uri("https://api.anthropic.com/v1/"),
    UpstreamModel = claudeModel, CredentialId = "claude-key",
    Protocols = LlmProtocol.ClaudeMessages
};
// Credentials = [new LlmCredential("claude-key", claudeApiKey)]
// SessionStore and the HTTP factory remain caller-owned, as for OpenAI/Ark.
var result = await loki.LLM.ClaudeMessages.CreateAsync(new ClaudeMessageRequest
{
    Model = "assistant", MaxTokens = 512,
    Messages = [new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "Hello" }]
}, new LlmCallOptions { SessionId = "claude-conversation" }, cancellationToken);
```

The target must be included in a route whose public `Model` is `assistant`. The model name and output limit are explicit caller choices. Authentication uses request-local `x-api-key` and `anthropic-version` headers; the default version is `2023-06-01`. Select another version with the constructor. Configure optional `anthropic-beta` headers on the named HTTP client `Asgard.Loki.LLM.Anthropic`. Changing version/client identity invalidates old Session bindings; change `LlmTarget.Version` when external HTTP defaults change semantics.

The provider references only `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not convert Chat/Responses or execute tools. Optional explicit conversion is supplied separately by `Asgard.Loki.LLM.Conversion`. Disable generation POST retries and hedging in the supplied HTTP factory.

Verification uses xUnit v3 with substitute HTTP handlers, fragmented SSE, `MemoryDistributedCache` and a test coordinator. No Anthropic live account/model verification has been performed. See [Messages API](https://platform.claude.com/docs/en/api/messages/create) and [streaming](https://platform.claude.com/docs/en/build-with-claude/streaming).

## Explicit authentication and HTTP policy

The existing four-parameter constructor remains binary-compatible and defaults to request-local `x-api-key`. An additive constructor accepts:

```csharp
var provider = new AnthropicLlmProvider(httpFactory, new AnthropicHttpOptions
{
    Authentication = AnthropicAuthentication.ApiKey, // or explicit Bearer
    MaximumBodyBytes = 32 * 1024 * 1024,
    MaximumBufferedTerminalEvents = 128,
    MaximumTrackedContentBlocks = 128
});
```

Bearer emits only `Authorization: Bearer`; Key mode emits only `x-api-key`. Both consume an ordinary `LlmCredential` API Key identity; anonymous/external credentials are rejected before factory dispatch. There is no identity discovery, expiring-token refresh, SigV4 fallback, combined `Key|region` parsing or host/model inference. Changing a static credential requires a fresh binding. An unselected authentication/signing default fails before send. Selected auth/version/Host replace same-name defaults on the individual request, while unrelated beta defaults remain caller-owned and unchanged.

Raw JSON/SSE reads are bounded, strict UTF-8 and duplicate-property checked, with exact declared lengths. Original HTTP/native complete errors retain status/body, including HTTP 200. Native source block indices/tool IDs/kinds and message closure are validated without assembling arguments or replacing text/signatures/encrypted data. `message_delta.stop_reason`, `message_stop` and unknown tails are withheld until real EOF, async source disposal, response/client disposal and final cancellation. A late native/transport error cannot release held success. Unknown typed metadata remains raw; it cannot reopen known content or replace a missing terminal. Initial deltas remain incremental and early exit releases only this call's resources. No retries, reconnect, tool execution or history management is added.

**Migration:** `anthropic-http-v2` binds explicit auth, API version, named client and all limits. Predecessor Anthropic Sessions require new IDs or expiry, including when wrapped by the converter. Original constructor signatures remain available. Core/other supplier identities and conversion-v25 algorithms are unchanged; the converter incorporates this provider's new identity normally. Update target Version when external default headers change semantics.

## Bedrock Messages endpoint reuse

The [AWS Messages contract](https://docs.aws.amazon.com/bedrock/latest/userguide/inference-messages-api.html) documents native Messages JSON/SSE and `x-api-key` with `anthropic-version: 2023-06-01`. Explicit roots for this SDK include:

| Endpoint | `LlmTarget.BaseAddress` | Actual model |
| --- | --- | --- |
| Standard Anthropic | `https://api.anthropic.com/v1/` | Caller-selected Claude model |
| Bedrock Runtime Messages | `https://bedrock-runtime.us-east-1.amazonaws.com/anthropic/v1/` | Caller-selected Runtime model/profile |
| Bedrock Mantle Messages | `https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/` | Caller-selected Mantle model |

Supply the final root prefix and exact model; this SDK appends only `messages`. It neither invents `/v1` nor prepends `anthropic.` or an AWS region to models. This is the Messages route, separate from `LLM.AWS` InvokeModel/Converse JSON/EventStream. It does not add an InvokeModel body version, remove caller-native fields or use binary AWS EventStream. Optional native workspace/beta headers belong to the named client; there is no workspace/project management API. Bearer is an explicit transport option for endpoints accepting it, not a certification of Bearer acceptance by every listed endpoint or model. Mantle SigV4, native Nova, OpenAI-compatible cloud endpoints, token counting and batch operations remain separate work.

Three independently authored full native manifests verify direct and public SDK JSON/SSE, exact paths/models/headers, tools/results, images/documents, thinking/signatures, schema settings, opaque state, large integers and unknown metadata. Auth/policy/session/fault tests bring this supplier to **126 offline tests**, repository **5794**, 28 packages. The original 22 cases remain. These are local protocol checks; no real Anthropic/AWS credentials, model behavior, account entitlement, Linux or remote Actions result is claimed.

Version, named client and options are serialized in a structured binding envelope; delimiter characters cannot let two different HTTP configurations share a Session identity. A real-cache regression reproduces the former collision and checks rejection before a second HTTP call.
