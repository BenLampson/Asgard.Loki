# Asgard.Loki.LLM.Volcengine

Volcengine Ark LLM provider for Asgard.Loki.

| API | Complete result | Streaming |
| --- | --- | --- |
| Chat Completions | Yes | SSE |
| Responses creation / continuation | Yes | SSE |

Create `VolcengineLlmProvider(IHttpClientFactory)` and register it in `LlmOptions.Providers`. Targets reference its `Id` and independently configure the server, actual model and credential. A typical base address is `https://ark.cn-beijing.volces.com/api/v3/`.

Uses the named client `Asgard.Loki.LLM.Volcengine`; disable generation POST retries/hedging in that client's configuration. Raw tools, reasoning, caching parameters, IDs, usage, errors and unknown events are retained. Response streams require an actual terminal event.

No native Responses WebSocket, protocol-to-protocol conversion or hard-coded model catalog. The supplier has 258 passing offline tests using local HTTP stubs. Separate real standard-Ark checks with `doubao-seed-evolving` passed Chat/Responses JSON/SSE, strict JSON Schema, tool calls with result return, thinking on/off, output limits, response continuation and opaque reasoning replay.

**Known live failure:** both protocols can return a function call despite a correctly transmitted `tool_choice="none"`; the strict integration tests remain failing. Loki preserves this upstream result and does not execute tools. Ark Responses currently reports token-limit truncation as `incomplete_details.reason="length"`, retained unchanged. See the [verification record](https://github.com/BenLampson/Asgard.Loki/blob/dev/docs/llm-parameter-verification.md) for the precise cases and limitations. Other accounts/models and Redis cross-process correctness require separate verification.

Depends only on the LLM capability, capability-independent Infrastructure and standard Microsoft HTTP components. See the repository root README for a complete initialization example.

## Transport dependency

This package references `Asgard.Loki.LLM`, `Asgard.Loki.Infrastructure` and standard HTTP support. Shared SSE framing is provided by Infrastructure; native requests, authentication, results and lifecycle remain supplier-owned.

## Native HTTP fidelity

Both existing model-target methods use supplier-owned bounded native JSON/SSE processing. The original constructor signature remains available; the explicit options constructor configures local byte/tail/association policies:

```csharp
var provider = new VolcengineLlmProvider(httpClientFactory, new VolcengineHttpOptions
{
    MaximumRequestBytes = 32 * 1024 * 1024,
    MaximumBodyBytes = 32 * 1024 * 1024,
    MaximumBufferedTerminalEvents = 128,
    MaximumTrackedChoices = 128
});
```

Complete HTTP(S) root, actual model/endpoint selector and independent API Key remain caller-selected LlmTarget data. Model names do not select another endpoint or lose a thinking suffix. Only actual model and method stream mode are transport-owned; all original messages/tool outputs/thinking/reasoning/encrypted/schema/cache/unknown fields retain their native types, exact values and order. Entire requests validate original Unicode/embedded JSON/duplicates/reserved aliases and freeze before factory creation or transport awaits. The returned native model field is not rewritten to the declared selector or public route.

HTTP JSON and SSE use strict UTF-8, duplicate-field and declared/raw-byte limits. Native HTTP200 root errors and HTTP failures preserve full original bytes/status/media in LlmUpstreamException, including binary errors. Selected Bearer, original method, full URI, Host and owned content remain checked through send/read/closure boundaries; other default key/signature authentication is rejected. The request owns its content/headers without changing borrowed factory defaults. Disable automatic redirects, POST retries and hedging in application-owned transports.

A per-choice finish_reason starts held-tail processing but does not invent global Chat DONE. The actual DONE is consumed as a transport control and reading continues through EOF; duplicate/premature DONE, extra data, late known generation and late native errors cannot produce a successful stream. Responses requires its own completed/incomplete/failed/error lifecycle, consistent known native response IDs/status and full original terminal snapshot. Unknown business response IDs and Chat extension type values are not adopted as native lifecycle identities. Sparse choice indices are held in a dictionary bounded by association count; they are never renumbered or used as array sizes. Tool IDs/indices/argument fragments stay original and are not assembled or executed.

Every ending-stage frame, including original usage and unknown tails, waits for normal framing/EOF, raw length/byte checks, owned stream/response/client/request closure and final target/cancellation checks. JSON results follow the same resource boundary. IO, asynchronous or synchronous disposal failures cannot first release terminal frames. No terminal/answer/token totals are synthesized, and no retry, target switch, credential borrowing or local business loop is added.

The ark-http-v2 implementation identity includes named client and all local policies. Existing model Sessions bind this identity as well as full server/model/credential configuration. Changing any policy or target requires a new logical Session; older unchecked-EOF bindings fail rather than silently rebinding. Factories, caches and explicit binding coordinators remain application-owned. The transport changes do not alter the capability/dependency architecture or add a generic model/audio coordinator.

207 offline xUnit v3 cases include independent complete request/result and raw SSE windows, reverse-linked tools, native reasoning/encrypted values, original length truncation, exact/zero/false/null/unknown fields, sparse associations and transport metadata. Faults cover late errors after DONE, missing/duplicate/changed-identity/late generation, raw errors, framing/UTF-8/declared-byte/tail/association budgets, final cancellation and async/sync/client disposal. Session tests use MemoryDistributedCache plus an explicit coordinator and verify policy/target changes and cross-instance native response ownership. These are local protocol checks, not a new live account/model/parameter or full-repository test result. The separate historical regular-Ark live results and strict known tool_choice=none failure above remain distinct.

## Published Bot native calls

`VolcengineBotClient` is a fixed-service native client in this package. Input model selects a published application, while the returned model and bot_usage may describe different remote model/endpoint selections. BotMetadata labels the application honestly; this client adds no fake LlmTarget, protocol, underlying-model Session or Responses ownership. It reuses this supplier's bounded native Chat transport and has no provider-peer dependency.

| Operation | Support |
| --- | --- |
| CompleteAsync | One native Bot chat/completions POST; full original JSON |
| StreamAsync | One native SSE POST; complete original JSON frames, transport identity and actual DONE |
| Messages/history/group-chat metadata/plugins/references/usage/unknown fields | Preserved as caller/server data; no local execution, aggregation or URL fetch |
| Target/authentication | Explicit complete Bots API root, published Bot ID, independent API Key, target/credential identity/version and named borrowed factory |
| Bot creation/administration, agent runtime/migration, polling, file management | Not implemented |
| Live Bot/model/plugin/setting validation | Not performed; offline protocol examples only |

```csharp
var bot = new VolcengineBotClient(httpClientFactory, new VolcengineBotOptions
{
    BaseAddress = new Uri("https://ark.cn-beijing.volces.com/api/v3/bots/"),
    BotId = publishedBotId,
    TargetId = "published-support-app",
    CredentialId = "bot-key"
}, botApiKey);
var result = await bot.CompleteAsync(new JsonObject
{
    ["messages"] = new JsonArray
    {
        new JsonObject { ["role"] = "user", ["content"] = "Hello" }
    }
}, cancellationToken);
```

The caller supplies an original messages array and any native optional parameters. Root model/stream and their aliases are reserved to the fixed Bot and explicit method. No model-name prefix, user, title, history, tools, plugin or generation default is inferred. Configuration and original input are snapshotted before factory/transport awaits; native group_chat_config endpoint references and opaque user_info stay data. The returned model field is never overwritten with Bot ID. Multiple model_usage/action_details records, counters, references and tool IDs retain their original values and order without billing or answer-quality interpretation.

Events preserve raw Data and immutable optional Json; actual [DONE] has Json=null. EventName/EventId/RetryMilliseconds remain separate from native request/plugin/tool/reference identities. A choice finish is not a global terminal. IsTerminal marks actual DONE or a native error after full EOF/length/encoding/resource/target/cancellation validation; it does not assert model success. Root business types from another protocol, including response.failed, do not become Bot failures. The factory remains borrowed; each HTTP request/client/response/body is owned. Limits come from HttpOptions and remain explicit, with no retry/failover/credential borrowing or execution loop.

The [official migration scope](https://docs.volcengine.com/docs/ark/migrate-from-bot-api?lang=en) retains calls to already-published Bot apps. New application creation and managed-agent migration are outside this SDK client. See [native Bot fields](https://docs.volcengine.com/docs/ark/bot-api?lang=zh&redirect=1) for actual service-specific behavior. Thirty-seven Bot offline cases verify independent full JSON/SSE windows, separate app/model identities, original metadata/tools/references/multi-endpoint data, actual DONE, snapshots/omission, errors, budgets, missing/late terminals and async/sync/final-cancellation closure. They do not establish actual Bot access or parameter/model effects.

## Explicit Coding Plan configuration

Reuse this provider with a separate complete target and credential. The SDK appends `chat/completions` or `responses` to the configured root:

```csharp
var coding = new VolcengineLlmProvider(httpClientFactory,
    id: "ark-coding", httpClientName: "Explicit.Ark.Coding");
var target = new LlmTarget
{
    Id = "coding-target", ProviderId = coding.Id,
    BaseAddress = new Uri("https://ark.cn-beijing.volces.com/api/coding/v3/"),
    UpstreamModel = codingModel, CredentialId = "coding-key",
    Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Responses
};
// Register this provider, target and independent codingKey in ordinary LlmOptions.
```

Chat JSON/SSE and existing Responses JSON/SSE retain full native tools/history/reasoning/schema/unknown values. Complete root, selected model, credential, version and named client remain part of the existing Session binding. No coding-plan alias, model suffix, host inference, account login, CLI configuration or model catalog is added. Native Messages uses the [existing Anthropic package](../Asgard.Loki.LLM.Anthropic/README.md#explicit-ark-coding-plan-messages) with explicit Bearer and its complete `coding/v1/` root; this supplier has no reference to that package.

Fourteen additional offline profile cases verify complete wire/results/events, both root trailing-slash forms, identity drift and cross-instance response ownership. Total: 258 cases (207 model HTTP, 37 published Bot, 14 Coding Plan). These verify transport fidelity only; current model entitlement, optional-setting acceptance and real Coding Plan behavior have not been tested. Endpoint contracts: [OpenCode configuration](https://docs.volcengine.com/docs/ark/coding-plan-personal-ai-opencode?lang=en), [ZCode configuration](https://docs.volcengine.com/docs/ark/coding-plan-personal-ai-zcode?lang=zh).
