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

The target must be included in a route whose public `Model` is `assistant`. The model name and output limit are explicit caller choices. Default authentication uses request-local `x-api-key` and `anthropic-version` headers; the default version is `2023-06-01`. Select another version with the constructor. Configure optional `anthropic-beta` headers on the named HTTP client `Asgard.Loki.LLM.Anthropic`. Changing version/client identity invalidates old Session bindings; change `LlmTarget.Version` when external HTTP defaults change semantics.

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

Bearer emits only `Authorization: Bearer`; Key mode emits only `x-api-key`. Both consume an ordinary LlmCredential API Key identity. The defaults reject anonymous/external credentials before dispatch; explicit [None mode](#explicit-native-messages-endpoint-and-authentication) requires LlmCredential.Anonymous. There is no identity discovery, expiring-token refresh, SigV4 fallback, combined `Key|region` parsing or host/model inference. Changing a static credential requires a fresh binding. An unselected authentication/signing default fails before send. Selected auth/version/Host replace same-name defaults on the individual request, while unrelated beta defaults remain caller-owned and unchanged.

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

Sixteen independent-window profile tests verify JSON/SSE, both root forms, full wire/data/transport metadata, native message_stop and late-failure gates, binding drift and cross-instance recovery. The supplier executable passes 440 offline cases (127 native,16 Coding Plan,9 Ollama,15 DashScope,12 DeepSeek,28 Moonshot/Kimi,16 MiniMax,24 Zhipu,30 native upstream 20 inference preset and143 configured-native cases). No live Coding Plan account/model/parameter validation has been performed. The [official configuration](https://docs.volcengine.com/docs/ark/coding-plan-personal-ai-claude-code?lang=zh) supplies the native service contract; its external client base URL convention differs because those clients append `/v1/messages`.

## Explicit Ollama Messages

Use the existing Bearer option with an explicit full target root. For this SDK, local BaseAddress is `http://localhost:11434/v1/` and hosted BaseAddress is `https://ollama.com/v1/`; the provider appends only `messages`. Construct `AnthropicLlmProvider(httpClientFactory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }, id: "ollama-messages", httpClientName: "Explicit.Ollama.Messages")` and register an ordinary `LlmTarget` with your exact model, independent CredentialId and `LlmProtocol.ClaudeMessages`.

The [official Messages guide](https://docs.ollama.com/api/anthropic-compatibility) accepts an explicit ignored Key placeholder for local SDK usage. Configure that value through an ordinary LlmCredential; no omitted Key is treated as anonymous. Local servers do not require authentication, while hosted Messages requires a real independent Bearer token and does not accept x-api-key alone. This provider's default API-Key mode stays unchanged; select Bearer explicitly. The native Ollama package separately supports explicit Anonymous credentials for its own native endpoints. There is no host/model/credential inference, login or model/server management.

Nine additional full-window offline cases verify JSON/SSE over local/proxy/hosted configured targets, exact path/version/Bearer, tools and reversed results, reasoning/signature/unknown values, original model versus target metadata, transport event/id/retry, native message_stop and root/key binding drift. Current total:440 passing offline cases. No real local/cloud model, endpoint entitlement or optional-setting acceptance has been tested. Upstream supports a subset: tool_choice, metadata, caching, citations, PDF and image-URL behavior must follow the actual service contract; preserving native fields is not a claim those settings work. There is no conversion to/from Ollama-native JSON/NDJSON in this package.

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

Fifteen full-window profile tests verify both header modes, JSON/SSE, complete paths, transport metadata, native message_stop, full original data, caller immutability and root/key Session drift. Current supplier total:440 passing offline cases. These are offline protocol checks, not live model/account/schema/reasoning/search validation. The package continues to depend only on its own LLM capability, Infrastructure and standard HTTP support.

## Explicit DeepSeek Messages

Use a separate complete target with `BaseAddress = new Uri("https://api.deepseek.com/anthropic/v1/")`, selected actual model, independent CredentialId and `LlmProtocol.ClaudeMessages`. This SDK appends only `messages`. Explicit proxy prefixes remain configuration. Construct `AnthropicLlmProvider(httpClientFactory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }, id: "deepseek-messages", httpClientName: "Explicit.DeepSeek.Messages")` and register it through ordinary LlmOptions with the target, Key and borrowed SessionStore. The default ApiKey mode is also supported; select one authentication mode explicitly.

The [native compatibility contract](https://api-docs.deepseek.com/guides/anthropic_api/) supports thinking and output_config.effort; budget_tokens is ignored and output_config.format is not a supported schema option. Parameter preservation does not establish their effect. Native thinking without a signature stays without a signature. Original tool_use/tool_result links and order, server_tool_use/web_search_tool_result links, user metadata, cache/usage counters and future fields remain intact. No Claude model-name mapping, effort normalization, fabricated signature, local tool/search execution or peer-provider dependency is added. [Official agent integration](https://api-docs.deepseek.com/quick_start/agent_integrations/claude_code/) also documents the explicit Bearer token mode; no client installation, login or configuration writer is part of this SDK.

Twelve independent-window cases cover JSON/SSE over native/proxy roots and both auth modes, whole wire/results/events, thinking/server-tool/client-tool identity, native message_stop, model-suffix fidelity and root/key Session drift. Current supplier total:440 passing offline cases. Missing message_stop fails without releasing held successful endings. Real account/model acceptance and reasoning/effort/search behavior remain unverified. Chat/Responses/beta FIM use the existing [OpenAI package](../Asgard.Loki.LLM.OpenAI/README.md#explicit-deepseek-native-endpoints) with their separately configured complete roots.

## Explicit Moonshot and Kimi Coding Messages

Use this existing provider with a selected complete target and `LlmProtocol.ClaudeMessages`. The provider appends only `messages`, so every SDK root includes `/v1/`:

| Service | Complete SDK BaseAddress |
| --- | --- |
| Moonshot China / international | `https://api.moonshot.cn/anthropic/v1/` / `https://api.moonshot.ai/anthropic/v1/` |
| Kimi Coding China / international | `https://api.kimi.com/coding/v1/` / `https://api.kimi.ai/coding/v1/` |

```csharp
var provider = new AnthropicLlmProvider(httpClientFactory,
    new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer },
    id: "moonshot-messages", httpClientName: "Explicit.Moonshot.Messages");
var target = new LlmTarget
{
    Id = "moonshot-messages-target", ProviderId = provider.Id,
    BaseAddress = new Uri("https://api.moonshot.ai/anthropic/v1/"),
    UpstreamModel = selectedMessagesModel, CredentialId = "selected-platform-key",
    Protocols = LlmProtocol.ClaudeMessages
};
// Register provider/target, independent platform Key and caller-owned SessionStore.
```

Select Bearer explicitly for the [native HTTP contract](https://platform.kimi.ai/docs/api/messages), or ApiKey for the documented Anthropic SDK header mode. Root/model/Key/auth/client changes remain complete Session identity changes. Coding roots, credentials and entitlement are independently selected; the SDK does not impersonate clients, configure an external CLI or discover credentials.

Original output_config.effort and format.json_schema, thinking/text/tool_use blocks, full prior thinking/signature/tool-result history, metadata and unknown fields remain native. Optional absent or explicit empty signatures retain that distinction. Top-level cache_control and nested markers are forwarded unchanged; the service documents only the top-level control as effective. Native input_tokens excludes cache reads/writes; cache_read_input_tokens, cache_creation_input_tokens, TTL breakdown and thinking token details remain separate original counters. No usage normalization or billing calculation occurs.

Twenty-eight whole-window/fault cases verify five explicit platform/Coding/proxy roots over both auth modes and JSON/SSE, original data and transport metadata, unsigned/empty-signature behavior, reversed tool results, native message_stop and root/key/model/auth/client Session drift. Current supplier total:440 passing offline cases. No live account/model, cache/schema/effort effectiveness or Coding access has been verified. Native Chat/Partial/Responses uses the [existing OpenAI package](../Asgard.Loki.LLM.OpenAI/README.md#explicit-moonshot-and-kimi-coding-native-endpoints), with no supplier-peer reference or local tool execution.

## Explicit MiniMax Messages

Use the existing provider with a complete `https://api.minimax.io/anthropic/v1/` target root, actual selected model, independent CredentialId and LlmProtocol.ClaudeMessages. The provider appends only messages; explicit China/proxy prefixes remain configuration. For selected Bearer use `new AnthropicLlmProvider(httpClientFactory, new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer }, id: "minimax-messages", httpClientName: "Explicit.MiniMax.Messages")`. The default ApiKey header mode is also supported. Register provider/target/independent Key and borrowed SessionStore through ordinary LlmOptions.

The [official Messages contract](https://platform.minimax.io/docs/api-reference/text-anthropic-api) requires preserving full thinking/text/tool_use history in subsequent calls. The SDK retains original thinking blocks without inventing signatures, output_config.effort, tool/result order/IDs, metadata and unknown values. Model-specific thinking/effort and media restrictions remain caller-owned. The service ignores temperature/top_p/top_k and several management settings; forwarding them does not prove their effect. No schema support is inferred from effort support, no client/CLI setup or local tool execution occurs.

Sixteen independent-window cases verify complete native/proxy roots, both auth modes and JSON/SSE, exact native wire/results/events, original thinking/effort/ignored sampling settings, reverse tool results, message_stop and root/key Session affinity. Current supplier total:440 passing offline cases. No real MiniMax model/account/optional-setting validation has been performed. Native v2 text/chatcompletion_v2 uses the independent [LLM.MiniMax package](../Asgard.Loki.LLM.MiniMax/README.md); no supplier-peer reference is required.

## Explicit Zhipu v4 native Messages

Configure AnthropicLlmProvider with the complete native `https://open.bigmodel.cn/api/anthropic/v1/` root, actual caller-selected model and independent Key. The [official compatibility guide](https://docs.bigmodel.cn/cn/guide/develop/claude/introduction) shows native x-api-key authentication. Bearer is also an explicit existing adapter mode for a caller-selected upstream; no host/model inference or credential discovery occurs.

| Operation | Support | Limits / live status |
| --- | --- | --- |
| Messages JSON / SSE | Original system/messages/content, thinking, tool associations, usage and extensions | Offline fake HTTP only; no real account/model validation |
| Authentication | Explicit ApiKey or Bearer; native anthropic-version remains configured by the adapter | Caller chooses the accepted mode and complete root |
| Stream terminal | Actual message_stop, then normal EOF/owned-resource closure/final cancellation | No EOF-generated stop, hidden tool loop or retry |
| Chat / Responses | Existing [OpenAI package profiles](../Asgard.Loki.LLM.OpenAI/README.md#explicit-zhipu-v4-native-chat-and-responses) | Separate native targets; no supplier-peer dependency |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Anthropic;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedModel and selectedMessagesKey are caller-owned.
var provider = new AnthropicLlmProvider(factory,
    new AnthropicHttpOptions { Authentication = AnthropicAuthentication.ApiKey },
    id: "zhipu-messages", httpClientName: "Explicit.Zhipu.Messages");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("zhipu-key", selectedMessagesKey)],
    Routes = [new LlmRoute
    {
        Model = "zhipu-messages", Targets = [new LlmTarget
        {
            Id = "zhipu-messages-selected", ProviderId = provider.Id,
            BaseAddress = new Uri("https://open.bigmodel.cn/api/anthropic/v1/"),
            UpstreamModel = selectedModel, CredentialId = "zhipu-key",
            Protocols = LlmProtocol.ClaudeMessages
        }]
    }]
}).Build();
var result = await loki.LLM.ClaudeMessages.CreateAsync(new ClaudeMessageRequest
{
    Model = "zhipu-messages", MaxTokens = 128,
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" })
}, new LlmCallOptions { SessionId = "zhipu-messages-1" }, cancellationToken);
```

Native tool_use/tool_result IDs, reversed caller results, input_json_delta fragments, thinking and optional signatures remain original. The SDK neither fabricates absent signatures nor guarantees support for every Anthropic option; the official guide notes differences. Preserve caller history explicitly and configure actual accepted settings. HTTP errors retain actual status and original UTF-8 body; no retry, implicit authentication switch or target fallback occurs. Missing native message_stop and late IO/final cancellation cannot release buffered endings; early exit disposes the current owned call only. Complete root/model/Key/provider/client/authentication/policy/version Session identity and existing byte limits apply. Cache, coordinator and HTTP factory remain borrowed; disable generation retries/hedging in the application pipeline. Independent native/proxy full JSON/SSE and boundary tests verify protocol handling offline, not real model or setting effects.

## Explicit native upstream Messages

Reuse AnthropicLlmProvider with an explicit complete native Messages root such as a selected `/v1/` or proxy prefix, actual model and independent upstream API Key. The provider appends messages. Choose ApiKey or Bearer authentication explicitly according to the configured upstream's accepted native contract; the SDK does not infer a mode from the hostname or add both modes automatically.

| Operation | Support | Limits / live status |
| --- | --- | --- |
| Messages JSON / SSE | Complete native system/history/tool IDs, original thinking/signatures and result metadata | Independent three-root offline fixtures; no real service/model validation |
| Native authentication | Explicit ApiKey (`x-api-key`) or Bearer, with native anthropic-version | Unselected default credentials remain rejected; no key discovery or fallback |
| Stream endings | Actual message_stop plus source EOF, owned closure and final cancellation | No synthetic stop, tool execution, retry or background consumption |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Anthropic;
using System.Text.Json.Nodes;

// factory, sessionStore, selectedMessagesApiRoot, selectedModel and selectedApiKey are caller-owned.
var provider = new AnthropicLlmProvider(factory,
    new AnthropicHttpOptions { Authentication = AnthropicAuthentication.ApiKey },
    id: "native-upstream-messages", httpClientName: "Explicit.NativeUpstream.Messages");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("upstream-key", selectedApiKey)],
    Routes = [new LlmRoute
    {
        Model = "upstream-messages", Targets = [new LlmTarget
        {
            Id = "selected-upstream-messages", ProviderId = provider.Id,
            BaseAddress = selectedMessagesApiRoot,
            UpstreamModel = selectedModel, CredentialId = "upstream-key",
            Protocols = LlmProtocol.ClaudeMessages, Version = "caller-context-v1"
        }]
    }]
}).Build();
var result = await loki.LLM.ClaudeMessages.CreateAsync(new ClaudeMessageRequest
{
    Model = "upstream-messages", MaxTokens = 128,
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" })
}, new LlmCallOptions { SessionId = "native-messages-1" }, cancellationToken);
```

Native reverse tool results, input_json_delta fragments, optional signatures and exact usage/extensions remain original. Absent signatures are not fabricated. Full root/model/Key/provider/client/authentication/policy/version Session affinity, raw HTTP errors, bounded native ending delivery and borrowed dependencies remain unchanged. Thirty new cases verify three complete roots, both explicit authentication modes, JSON/SSE, missing message_stop, root/key/model/auth/client/version/policy drift, replay, late read/final cancellation, original errors and early exit; current supplier total is 440 passing offline cases. Actual hosted model/parameter/ciphertext support is unverified. Use separate [OpenAI-compatible native targets](../Asgard.Loki.LLM.OpenAI/README.md#explicit-native-upstream-chat-responses-text-and-compact), [Gemini targets](../Asgard.Loki.LLM.Google/README.md#native-http-and-ending-policies) and [API-Key search](../Asgard.Loki.LLM.NewAPI/README.md) for those distinct operations, without provider-peer references or gateway coordination.

## Explicit vLLM and SGLang Messages

For a deployment explicitly serving native `/v1/messages`, use the [native target initialization](#explicit-native-upstream-messages) with the complete prefixed `/v1/` root, actual served model, independent Key and ClaudeMessages protocol. Select Bearer authentication explicitly for these inference presets:

```csharp
var provider = new AnthropicLlmProvider(factory,
    new AnthropicHttpOptions { Authentication = AnthropicAuthentication.Bearer },
    id: "inference-messages", httpClientName: "Explicit.Inference.Messages");
var target = new LlmTarget
{
    Id = "selected-inference-messages", ProviderId = provider.Id,
    BaseAddress = selectedCompleteV1Root, UpstreamModel = selectedServedModel,
    CredentialId = "inference-key", Protocols = LlmProtocol.ClaudeMessages,
    Version = "deployment-context-v1"
};
// Register provider, target, caller API-Key credential and Session dependencies as above.
```

| Operation | Support | Limits / live status |
| --- | --- | --- |
| Messages JSON / SSE | Original history, thinking/signatures, reverse tool results, fragments and native metadata | Deployment must implement native Messages; no automatic protocol detection |
| Native headers | Selected Bearer Key and anthropic-version | No unselected credentials, anonymous/dummy-Key inference or dual-auth injection |
| Ending / Session | Actual message_stop, EOF/owned closure/final cancellation; complete target and auth affinity | No retry, hidden history, model provisioning or background consumption |

Twenty additional offline cases verify two complete roots, whole JSON/SSE windows, missing native stop, replay, server/model/Key/auth/client/policy/version drift, late read/final cancellation, original HTTP errors and early exit. Current supplier total: 440 passing offline cases. Existing request/body/tail limits and borrowed factory/cache/coordinator ownership apply. No real deployment/model/optional-setting acceptance has been verified. Chat/text/Responses remain in the [OpenAI package](../Asgard.Loki.LLM.OpenAI/README.md#explicit-vllm-and-sglang-native-presets); packages do not reference each other.

## Explicit native Messages endpoint and authentication

AnthropicHttpOptions.Authentication remains the single authentication selector. Its original ApiKey/Bearer values keep their numeric values and default behavior; additional None/Header/Query modes are explicit choices for a caller-selected existing native upstream. Optional Transport supplies AnthropicHttpTransportOptions with MessagesPath and non-secret credential formatting. This provider still speaks native Messages and performs no endpoint/protocol detection or conversion.

| Operation / mode | Support | Limits / live status |
| --- | --- | --- |
| Native Messages JSON / SSE | Complete native body/history/result/events, including thinking/signatures, reverse tool IDs and exact unknown fields | Existing body/tail/block limits; no real deployment/model verification |
| ApiKey / Bearer | Selected x-api-key or Authorization: Bearer | Ordinary API-Key credential; original constructors/defaults retained |
| Header | CredentialName header, CredentialPrefix + Key + CredentialSuffix | Explicit API Key; control/structural/version header names rejected |
| Query | Escaped CredentialName and formatted Key appended once | Explicit API Key; conflicting encoded/case-variant fixed credential parameters rejected |
| None | Explicit LlmCredential.Anonymous, no selected credential | Missing Key never implies anonymous; external account identities remain unsupported |

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Anthropic;

// factory, sessionStore, selectedCompleteRoot, selectedModel and selectedApiKey are caller-owned.
var provider = new AnthropicLlmProvider(factory, new AnthropicHttpOptions
{
    Authentication = AnthropicAuthentication.Query,
    Transport = new AnthropicHttpTransportOptions
    {
        MessagesPath = "native/messages.run?existing=1",
        CredentialName = "api_key"
    }
}, id: "native-messages", httpClientName: "Explicit.Native.Messages");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore, Providers = [provider],
    Credentials = [new LlmCredential("selected-key", selectedApiKey)],
    Routes = [new LlmRoute
    {
        Model = "public", Targets = [new LlmTarget
        {
            Id = "selected-native", ProviderId = provider.Id,
            BaseAddress = selectedCompleteRoot, UpstreamModel = selectedModel,
            CredentialId = "selected-key", Protocols = LlmProtocol.ClaudeMessages,
            Version = "deployment-context-v1"
        }]
    }]
}).Build();
```

MessagesPath is appended beneath the complete target root, preserving its proxy prefix; one leading slash has the same append semantics. Fixed queries retain original order/values and repeated business parameters, including literal URL values. Absolute/scheme-relative addresses, fragments, traversal, templates, malformed percent/UTF-8 escapes and control characters are rejected. Select another complete target to change servers. Header and Query require a name; ApiKey/Bearer/None reject unused credential formatting. Header values use printable ASCII; query values can contain Unicode and reserved characters and are escaped once.

For no authentication, set Authentication = AnthropicAuthentication.None and register LlmCredential.Anonymous("selected-key"). Transport can be omitted for None to retain the native messages path. Header/Query require Transport with a CredentialName. Do not put a Key in prefix/suffix or fixed query; the actual Key remains in LlmCredential. No dummy credential, account source, discovery or fallback is introduced.

Complete target, actual model, credential identity, API version, named client, frozen path/query/auth formatting, local limits and target Version participate in the existing Session binding. Transport = null is omitted from options JSON and preserves previous ApiKey/Bearer binding bytes. Supplying Transport explicitly opts into stricter actual-wire verification and produces a distinct binding even for a default path. Changed semantics reject old Sessions before another HTTP send. Update target Version for identity-affecting application pipeline changes under a stable client name.

Configured calls freeze the body, exact URI/Host, selected credential, anthropic-version and Accept before factory creation. Same-name defaults are overridden on the request; unselected authentication/signature/Cookie defaults fail without changing factory settings. Actual method/URI/Host/auth/version/content are rechecked across native events and final resource closure. Raw HTTP/late native errors preserve original status, media and arbitrary bytes in LlmUpstreamException.Body. Native message_delta/message_stop and unknown tails still wait for actual EOF, owned closure and final cancellation. Factory/cache/coordinator remain borrowed; early exit only closes the current call. Disable automatic retries, redirects, hedging or hidden auth changes in the application HTTP pipeline.

The existing explicit Conversion decorator delegates native credential support and includes these settings in its binding. No protocol or model is inferred from MessagesPath. There is no local relay, route administration, model discovery, tool execution, hidden history or cross-provider dependency.

143 additional offline supplier cases compare an independently retained literal native window over five authentication modes and JSON/SSE, including original tools/signature omissions/unknown values, native terminal failures, raw binary errors, fixed queries, complete binding drift/replay, selected/default credentials, actual request mutation, early exit and late EOF/final cancellation. Current supplier total: 440 passing offline cases. These tests establish protocol fidelity; actual service/model/optional-setting/signature acceptance remains unverified.
