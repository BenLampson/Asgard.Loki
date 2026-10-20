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

The provider references only `Asgard.Loki.LLM`, capability-independent `Asgard.Loki.Infrastructure` and standard Microsoft HTTP components. It does not convert Chat/Responses or execute tools. Optional explicit conversion is supplied separately by `Asgard.Loki.LLM.Conversion`. Disable generation POST retries and hedging in the supplied HTTP factory.

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

Supply the final root prefix and exact model; this SDK appends only `messages`. It neither invents `/v1` nor prepends `anthropic.` or an AWS region to models. This is the Messages route, separate from `LLM.AWS` InvokeModel/Converse JSON/EventStream. It does not add an InvokeModel body version, remove caller-native fields or use binary AWS EventStream. Optional native workspace/beta headers belong to the named client; there is no workspace/project management API. Bearer is an explicit transport option for endpoints accepting it, not a certification of Bearer acceptance by every listed endpoint or model. Mantle SigV4 is supplied separately by [the AWS Mantle adapter](../Asgard.Loki.LLM.AWS/README.md#explicit-mantle-sigv4-messages). Native Nova, OpenAI-compatible cloud endpoints, token counting and batch operations remain separate work.

Three independently authored full native manifests verify direct and public SDK JSON/SSE, exact paths/models/headers, tools/results, images/documents, thinking/signatures, schema settings, opaque state, large integers and unknown metadata. The original 22 cases remain. These are local protocol checks; no real Anthropic/AWS credentials, model behavior, account entitlement, Linux or remote Actions result is claimed.

Version, named client and options are serialized in a structured binding envelope; delimiter characters cannot let two different HTTP configurations share a Session identity. A real-cache regression reproduces the former collision and checks rejection before a second HTTP call.

A subsequent fixture audit aligns the full stream's content indices with its complete native `content` array (0–3). A separate raw passthrough regression retains sparse int.MaxValue indices without indexed-array allocation; it does not certify a complete Claude array or expand strict cross-protocol conversion. Separate converter tests verify high/off reasoning and Schema transmission through explicitly selected authentication without new supplier dependencies.

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.

## Explicit Ark Coding Plan Messages

Use this existing native Messages provider with explicitly selected Bearer authentication and the complete SDK root below. This SDK appends only `messages`, so the root includes `/v1/`:

```csharp
var coding = new AnthropicLlmProvider(httpClientFactory,
    new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer },
    id: "coding-messages", httpClientName: "Explicit.Coding.Messages");
var target = new LlmTarget
{
    Id = "coding-messages-target", ProviderId = coding.Id,
    BaseAddress = new Uri("https://ark.cn-beijing.volces.com/api/coding/v1/"),
    UpstreamModel = codingModel, CredentialId = "coding-key",
    Protocols = LlmProtocol.ClaudeMessages
};
// Register provider/target and independent codingKey through ordinary LlmOptions.
```

The request emits one Bearer header, no x-api-key, and the explicit/default anthropic-version. Original thinking/signatures, tool-use/result links and order, schema settings, exact integers and future fields remain native; the SDK does not convert them, choose models or certify endpoint acceptance of optional parameters. Returned native model data remains distinct from selected-target metadata. Full root/model/key/auth/version/client changes invalidate old Sessions normally. No reference to LLM.Volcengine, brand wrapper, CLI/login/settings writer, tool executor or retry loop is added.

Sixteen independent-window profile tests verify JSON/SSE, both root forms, full wire/data/transport metadata, native message_stop and late-failure gates, binding drift and cross-instance recovery. The supplier executable passes 179 offline cases (127 native,16 Coding Plan,9 Ollama,15 DashScope and12 DeepSeek profile cases). No live Coding Plan account/model/parameter validation has been performed. The [official configuration](https://docs.volcengine.com/docs/ark/coding-plan-personal-ai-claude-code?lang=zh) supplies the native service contract; its external client base URL convention differs because those clients append `/v1/messages`.

## Explicit Ollama Messages

Use the existing Bearer option with an explicit full target root. For this SDK, local BaseAddress is `http://localhost:11434/v1/` and hosted BaseAddress is `https://ollama.com/v1/`; the provider appends only `messages`. Construct `AnthropicLlmProvider(httpClientFactory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }, id: "ollama-messages", httpClientName: "Explicit.Ollama.Messages")` and register an ordinary `LlmTarget` with your exact model, independent CredentialId and `LlmProtocol.ClaudeMessages`.

The [official Messages guide](https://docs.ollama.com/api/anthropic-compatibility) accepts an explicit ignored Key placeholder for local SDK usage. Configure that value through an ordinary LlmCredential; no omitted Key is treated as anonymous. Local servers do not require authentication, while hosted Messages requires a real independent Bearer token and does not accept x-api-key alone. This provider's default API-Key mode stays unchanged; select Bearer explicitly. The native Ollama package separately supports explicit Anonymous credentials for its own native endpoints. There is no host/model/credential inference, login or model/server management.

Nine additional full-window offline cases verify JSON/SSE over local/proxy/hosted configured targets, exact path/version/Bearer, tools and reversed results, reasoning/signature/unknown values, original model versus target metadata, transport event/id/retry, native message_stop and root/key binding drift. Current total:179 passing offline cases. No real local/cloud model, endpoint entitlement or optional-setting acceptance has been tested. Upstream supports a subset: tool_choice, metadata, caching, citations, PDF and image-URL behavior must follow the actual service contract; preserving native fields is not a claim those settings work. There is no conversion to/from Ollama-native JSON/NDJSON in this package.

## Explicit DashScope Messages

Use the existing provider with a complete `apps/anthropic/v1/` root and an independent actual model/credential target. This SDK appends only `messages`, so its complete root includes `/v1/`. The [native Messages contract](https://help.aliyun.com/zh/model-studio/anthropic-api-messages) supports either request-local x-api-key or explicit Bearer; select one through AnthropicHttpOptions.Authentication.

```csharp
var provider = new AnthropicLlmProvider(httpClientFactory,
    new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer },
    id: "dashscope-messages", httpClientName: "Explicit.Ali.Messages");
var target = new LlmTarget
{
    Id = "dashscope-messages-target", ProviderId = provider.Id,
    BaseAddress = new Uri("https://workspace-id.cn-beijing.maas.aliyuncs.com/apps/anthropic/v1/"),
    UpstreamModel = selectedMessagesModel, CredentialId = "dashscope-key",
    Protocols = LlmProtocol.ClaudeMessages
};
// Register provider/target and independent dashscopeKey through ordinary LlmOptions.
```

Original output_config.format schema/effort, thinking, tools/results and their order, explicit empty signatures, cache counters and future data remain native. They are not converted to another protocol or selected through model substrings. No automatic web-search tool insertion, workspace/model discovery, CLI configuration, login or local tool execution occurs. Each model's protocol and optional-setting support remains caller-owned; passing a field does not certify its effect. Legacy roots and explicit proxy prefixes are supported configuration without automatic migration.

Fifteen full-window profile tests verify both header modes, JSON/SSE, complete paths, transport metadata, native message_stop, full original data, caller immutability and root/key Session drift. Current supplier total:179 passing offline cases. These are offline protocol checks, not live model/account/schema/reasoning/search validation. The package continues to depend only on its own LLM capability, Infrastructure and standard HTTP support.

## Explicit DeepSeek Messages

Use a separate complete target with `BaseAddress = new Uri("https://api.deepseek.com/anthropic/v1/")`, selected actual model, independent CredentialId and `LlmProtocol.ClaudeMessages`. This SDK appends only `messages`. Explicit proxy prefixes remain configuration. Construct `AnthropicLlmProvider(httpClientFactory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }, id: "deepseek-messages", httpClientName: "Explicit.DeepSeek.Messages")` and register it through ordinary LlmOptions with the target, Key and borrowed SessionStore. The default ApiKey mode is also supported; select one authentication mode explicitly.

The [native compatibility contract](https://api-docs.deepseek.com/guides/anthropic_api/) supports thinking and output_config.effort; budget_tokens is ignored and output_config.format is not a supported schema option. Parameter preservation does not establish their effect. Native thinking without a signature stays without a signature. Original tool_use/tool_result links and order, server_tool_use/web_search_tool_result links, user metadata, cache/usage counters and future fields remain intact. No Claude model-name mapping, effort normalization, fabricated signature, local tool/search execution or peer-provider dependency is added. [Official agent integration](https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code/) also documents the explicit Bearer token mode; no client installation, login or configuration writer is part of this SDK.

Twelve independent-window cases cover JSON/SSE over native/proxy roots and both auth modes, whole wire/results/events, thinking/server-tool/client-tool identity, native message_stop, model-suffix fidelity and root/key Session drift. Current supplier total:179 passing offline cases. Missing message_stop fails without releasing held successful endings. Real account/model acceptance and reasoning/effort/search behavior remain unverified. Chat/Responses/beta FIM use the existing [OpenAI package](../Asgard.Loki.LLM.OpenAI/README.md#explicit-deepseek-native-endpoints) with their separately configured complete roots.
