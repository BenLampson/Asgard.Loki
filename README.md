# Asgard.Loki

[![CI](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml/badge.svg)](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

A modular .NET 10 / C# 14 client library for model APIs. Use one `LokiClient` and install only the capabilities and providers you need.

The architecture contract, UML, dependency rules and provider extension requirements live in [docs/architecture.md](docs/architecture.md). All namespaces start with `Asgard`.

The New API comparison is limited to [source-proven SDK gaps](docs/model-call-roadmap.md#scope-correction-after-the-source-recheck-2026-10-20); newer supplier features and exhaustive conversion combinations are optional extensions. Complete [Gemini ↔ Chat candidate-array conversion](src/Asgard.Loki.LLM.Conversion/README.md#complete-gemini--chat-candidate-arrays) now preserves source indices/order, independent tools/finishes/citations and shared usage. Gemini `candidateCount` → Chat `n` now works through the existing fixed-Chat SDK, alongside reasoning/Schema settings. Multi-candidate streams and the opposite request-count mapping remain separate work.

## Supported capabilities

| Capability | Provider package | Implemented APIs | Streaming |
| --- | --- | --- | --- |
| LLM | `Asgard.Loki.LLM.Volcengine` | Ark Chat Completions; Responses creation and continuation | SSE |
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions; Responses creation/continuation; explicit native compaction and WebSocket | SSE; JSON compaction; native WebSocket events |
| LLM | `Asgard.Loki.LLM.Anthropic` | Native Claude Messages | SSE |
| LLM | `Asgard.Loki.LLM.Google` | Native Gemini generateContent | SSE |
| LLM | `Asgard.Loki.LLM.Ollama` | Native Chat/generate JSON/NDJSON, native thinking/schema/tool history and prompt/context; explicit anonymous or Bearer identity | — |
| LLM | `Asgard.Loki.LLM.Azure` | Native v1/dated Chat and Responses JSON; api-key or borrowed external token authentication | Native SSE; delayed filter/usage tails |
| LLM | `Asgard.Loki.LLM.GoogleVertex` | Vertex Gemini/Claude JSON/SSE with borrowed tokens; separate Express Gemini JSON/SSE with explicit API Key; MaaS Chat JSON/SSE with borrowed tokens | Native events with delayed terminal checks |
| LLM | `Asgard.Loki.LLM.AWS` | Native Bedrock Claude and explicit Converse JSON/EventStream with borrowed SigV4 or explicit Bedrock API Key credentials | Native binary EventStream with independent protocol lifecycles |
| LLM conversion | `Asgard.Loki.LLM.Conversion` | Explicit common-subset request/response conversion between Chat, Responses, Claude and Gemini | Text deltas and complete function calls |
| Speech | `Asgard.Loki.Audio.Volcengine` | Volcengine V3 text-to-speech; API Key or legacy app/token authentication | HTTP chunked JSON to audio chunks |
| Speech | `Asgard.Loki.Audio.OpenAI` | Native Speech binary/SSE responses; custom voice objects and complete native JSON settings | Incremental binary bytes or native delta/done events |
| File transcription / translation | `Asgard.Loki.Audio.OpenAI` | Native multipart, complete JSON/verbose/diarized or text/subtitle results | Native transcription delta/segment/done SSE |
| Images | `Asgard.Loki.Image.OpenAI` | OpenAI image generations and native JSON/multipart edits; Base64 and URL results | Native generation/edit SSE partial/final events |
| Embedding | `Asgard.Loki.Embedding.OpenAI` | Native Embeddings, text/token batch inputs, Float/Base64 and indexed results | — |
| Embedding | `Asgard.Loki.Embedding.Google` | Native embedContent / synchronous batchEmbedContents, native settings/media JSON and ordered vectors | — |
| Embedding | `Asgard.Loki.Embedding.Ollama` | Native embed text/media batches, original settings, ordered vectors and optional native counters | — |
| Embedding | `Asgard.Loki.Embedding.Jina` | Native text/media/fused groups, indexed dense and per-token Float/Base64 embeddings | — |
| Rerank | `Asgard.Loki.Rerank.Cohere` | Explicit native v1/v2; original document indices/scores and full JSON | — |
| Rerank | `Asgard.Loki.Rerank.Jina` | Native text/multimodal/listwise; original indices, optional document vectors and full JSON | — |
| Rerank | `Asgard.Loki.Rerank.Ali` | Native DashScope nested text/media requests, original indices/documents, scores and usage | — |
| Rerank | `Asgard.Loki.Rerank.SiliconFlow` | Native scalar/text/image requests, original indices/documents, optional native token metadata | — |
| Video | — | Project scaffold only | — |

Audio exposes an optional [native JSON speech contract](src/Asgard.Loki.Audio/README.md), with independent snapshots and delayed success until source enumeration/disposal finishes. [OpenAI speech](src/Asgard.Loki.Audio.OpenAI/README.md) implements native binary HTTP and explicit SSE aggregation/streaming, including custom voice objects and preserved audio/event data. Binary frames are SDK HTTP records; SSE envelopes retain native JSON/raw text, event identity and original usage.

Native requests preserve unknown JSON parameters. Native results retain complete upstream JSON, including tools, reasoning, usage, errors and future events. Public model aliases and actual upstream models are exposed separately. Optional conversion requires explicit registration, rejects unsupported fields by default, and retains upstream JSON and diagnostics separately from the converted result. User inline PDF inputs map across four protocols, and explicitly declared UTF-8 text files map between Responses, Claude and Gemini; native filenames, private references and metadata have explicit preservation/loss boundaries, with no SDK file download. Complete Chat/Responses URL citations map with unchanged-text ranges or safe ASCII-prefix rebasing; declared endpoint units now enable exact Unicode offsets and complete Gemini Web grounding ↔ Chat/Responses citations. Responses ↔ Gemini citation streams additionally support an explicit single cumulative text-part convention and declared OpenAI endpoint index unit; a separate opt-in [text-part concatenation](src/Asgard.Loki.LLM.Conversion/README.md#explicit-concatenation-of-responses-text-parts) maps sequential Responses text blocks into that cumulative target with exact citation rebasing; other stream/history pairs retain documented boundaries. See the [conversion support matrix](src/Asgard.Loki.LLM.Conversion/README.md).

Image exposes separate native JSON and caller-owned multipart editing contracts, including OpenAI native event streaming; see the [Image README](src/Asgard.Loki.Image/README.md#native-image-editing-contracts). The bundled OpenAI provider implements native JSON/multipart nonstream and SSE edits; generation and editing now hold native completion through EOF and transport disposal. Image variations, music generation, video generation and automatic request replay are **not implemented**. Audio exposes native file-operation contracts and an OpenAI multipart provider; see the [Audio README](src/Asgard.Loki.Audio/README.md#native-file-transcription-and-translation-contracts). Gateway administration and billing are outside this SDK. Supported model names and account entitlements are configured by the caller and validated by the actual provider; there is no hard-coded model catalog.

Explicit Claude/Gemini tool-error history now supports two caller-declared formats: a complete Gemini response JSON object, or original error text. Native failure flags, original call IDs and reverse-order parallel associations stay intact; ordinary success objects cannot be implicitly upgraded by an error key. Default conversion and Chat/Responses error destinations remain strict boundaries. See the [error format matrix](src/Asgard.Loki.LLM.Conversion/README.md#explicit-claudegemini-tool-errors).

Public LLM snapshots reject malformed original Unicode before JSON serialization and Session/cache work, including tool IDs, error text, unknown DOM/extension fields and optional compaction/WebSocket input. Valid Unicode and existing valid Session fingerprints stay unchanged. See the [encoding contract](src/Asgard.Loki.LLM/README.md#native-json-snapshot-encoding).

LLM additionally exposes an explicit [external authentication identity](src/Asgard.Loki.LLM/README.md#external-authentication-identity) for provider-owned token/signing sources. Core Sessions bind the stable principal and configuration, never a rotating access token. Existing API Key/anonymous contracts and fingerprint bytes stay compatible; a dedicated cloud provider must opt in and implement its own transport/authentication.

The independent [Azure LLM provider](src/Asgard.Loki.LLM.Azure/README.md) now implements complete native Chat/Responses JSON and SSE with explicit v1/dated routing and API Key or borrowed external token authentication. Stable principal Sessions survive token rotation; configuration/principal changes fail. Native SSE preserves delayed filter/usage tails and holds terminal events through EOF/disposal; compaction and WebSocket remain pending; no live Azure authentication or model validation is claimed.

The independent [Vertex LLM provider](src/Asgard.Loki.LLM.GoogleVertex/README.md) implements native Gemini/Claude JSON/SSE with explicit full project/location/model resources and borrowed external authentication. It preserves native fields and fixed-target Sessions through token rotation. Express Gemini API Key JSON/SSE is now available through a separate provider in the same package; MaaS Chat JSON/SSE is also delivered; wire fixtures do not establish live acceptance of Developer API-only fields.

## Packages and dependencies

| Package | Responsibility |
| --- | --- |
| `Asgard.Loki.Abstractions` | Generic client and module builder contracts |
| `Asgard.Loki` | Client implementation and module lifecycle |
| `Asgard.Loki.LLM` | Six native LLM protocols, routing, Session storage and registration |
| `Asgard.Loki.Audio` | Independent speech and native file-operation contracts, routing and results |
| `Asgard.Loki.Image` | Independent image contracts, routing and results |
| `Asgard.Loki.Embedding` | Independent native embedding contracts, routing and results; custom adapters |
| `Asgard.Loki.Rerank` | Independent native reranking contracts, routing and results; custom adapters |
| `Asgard.Loki.Rerank.Cohere` | Native Cohere v1/v2 reranking and provider-owned ranking views |
| `Asgard.Loki.Rerank.Jina` | Native Jina text/multimodal/listwise reranking and provider-owned views |
| `Asgard.Loki.Rerank.Ali` | Native DashScope nested reranking and provider-owned ranking views |
| `Asgard.Loki.Rerank.SiliconFlow` | Native SiliconFlow scalar/text/image reranking and provider-owned views |
| `Asgard.Loki.Embedding.OpenAI` | Native OpenAI embeddings provider; provider-owned vector views |
| `Asgard.Loki.Embedding.Google` | Native Google single/synchronous-batch embeddings and provider-owned views |
| `Asgard.Loki.Embedding.Ollama` | Native Ollama embedding inputs/settings, ordered vectors and provider-owned views |
| `Asgard.Loki.Embedding.Jina` | Native Jina text/media/fused inputs, dense/per-token embeddings and provider-owned views |
| `Asgard.Loki.Video` | Reserved video capability scaffold |
| `Asgard.Loki.LLM.Volcengine` | Ark LLM provider |
| `Asgard.Loki.LLM.OpenAI` | OpenAI LLM provider |
| `Asgard.Loki.LLM.Anthropic` | Native Claude Messages provider |
| Asgard.Loki.LLM.Google | Native Gemini Developer API provider |
| Asgard.Loki.LLM.Ollama | Native Ollama Chat/generate JSON/NDJSON; explicit anonymous or Bearer authentication |
| `Asgard.Loki.LLM.Azure` | Native Azure endpoints and provider-owned external token source |
| `Asgard.Loki.LLM.GoogleVertex` | Native Vertex Gemini, Claude, Express and MaaS target identity/authentication |
| `Asgard.Loki.LLM.AWS` | Native Bedrock Claude and Converse JSON/EventStream with borrowed External SigV4 or explicit Bedrock API Key credentials |
| `Asgard.Loki.LLM.Conversion` | Optional explicit conversion; references only LLM |
| `Asgard.Loki.Audio.Volcengine` | Volcengine speech provider |
| `Asgard.Loki.Audio.OpenAI` | Native OpenAI speech and multipart transcription/translation; independent Audio state |
| `Asgard.Loki.Image.OpenAI` | OpenAI image provider |

The entry package depends only on abstractions. Capability packages do not reference each other. Each provider depends only on its capability and standard Microsoft components. Installing OpenAI images does not install LLM or audio providers.

These are the package identities produced by this checkout. The PR/release CI configuration builds and verifies all twenty-eight packages when invoked and attaches them to that workflow run. The separate manual `Publish NuGet` workflow supports OIDC login verification and explicit publication; its default `verify` mode does not upload packages. See [release instructions](docs/releasing.md). Local preview packages can be created with `dotnet pack`.

## Native Claude, Gemini and explicit conversion

Loki is an embeddable SDK. Applications supply credentials, HTTP factories and Session dependencies; this library exposes no forwarding server or administration/billing services.

Use `loki.LLM.ClaudeMessages.CreateAsync/StreamAsync` with `ClaudeMessageRequest`, or `loki.LLM.Gemini.GenerateAsync/StreamAsync` with `GeminiContentRequest`. Configure the corresponding target's protocol explicitly:

| Provider | API root | Target protocol | Native request |
| --- | --- | --- | --- |
| `AnthropicLlmProvider` | `https://api.anthropic.com/v1/` | `LlmProtocol.ClaudeMessages` | `Messages`, explicit `MaxTokens`, extension `system`/tools/thinking |
| `GoogleLlmProvider` | `https://generativelanguage.googleapis.com/v1beta/` | `LlmProtocol.Gemini` | `Contents`, extension `systemInstruction`/tools/generationConfig |

The original target default still enables Chat/Responses; set `Protocols` explicitly for Claude/Gemini. Gemini's upstream model is a bare ID, mapped into the URL; its native body has neither `model` nor `stream`. See the provider [Claude](src/Asgard.Loki.LLM.Anthropic/README.md) and [Gemini](src/Asgard.Loki.LLM.Google/README.md) READMEs for initialization and limits.

To opt into conversion, register a `ConvertingLlmProvider(nativeProvider, upstreamProtocol, id, options)` from `Asgard.Loki.LLM.Conversion` and point the target at that ID. `LlmTarget.Protocols` then describes the caller-facing protocol; the decorator fixes the actual upstream protocol. The full target and conversion configuration are bound to the Session. Existing native calls never opt into conversion automatically.

```csharp
var converting = new ConvertingLlmProvider(
    new AnthropicLlmProvider(httpFactory), LlmProtocol.ClaudeMessages,
    "chat-via-claude", new LlmConversionOptions { DefaultMaxTokens = 512 });
// Register converting, configure a Claude target with Protocols = ChatCompletions,
// then use the ordinary loki.LLM.ChatCompletions interface with compatible history.
```

Add `using Asgard.Loki.LLM;`, `using Asgard.Loki.LLM.Anthropic;` and `using Asgard.Loki.LLM.Conversion;` for this example. The [conversion README](src/Asgard.Loki.LLM.Conversion/README.md) documents the twelve-direction common subset, diagnostics, pure data API and restrictions. Unsupported fields fail by default; signed/encrypted reasoning and cross-protocol response chains always require native APIs. Converted results expose original upstream JSON and diagnostics in `Metadata.Conversion`. Text streams incrementally; function arguments are emitted when complete. All new provider/conversion verification is offline, not live model validation.

Session fingerprints now include `ILlmProvider.BindingIdentity`. Bindings created before this change will fail validation and require new Session IDs or caller-controlled expiration/removal. The SDK does not silently rebind them.

Explicit conversion now also maps qualitative reasoning effort, representable Claude/Gemini numeric or dynamic/off budgets, summary/display intent and JSON Schema output wrappers across the four protocols. Schema constraints remain unchanged. Gemini modern `responseFormat.text` is recognized; choose `GeminiOutputFormat.ResponseFormat` explicitly for that target envelope, while the default remains the legacy JSON Schema envelope. OpenAI targets require `DefaultSchemaName` for unnamed schemas; unsupported wrapper metadata/enforcement or budget-to-effort replacement is diagnosed and requires explicit loss consent. Claude manual thinking requires caller-selected `TargetThinkingBudgetTokens` and a sufficient output limit. Model support is caller-selected; the SDK never rewrites model suffixes, infers budgets or increases limits. See [generation settings and limits](src/Asgard.Loki.LLM.Conversion/README.md#explicit-generation-settings).

## Initialize LLM

Install the entry package and the chosen LLM provider package. The example below uses Volcengine; substitute `OpenAiLlmProvider` and the OpenAI `/v1/` base address for OpenAI.

Provide an `IDistributedCache`, an `ILlmSessionLock` implementation, and an `IHttpClientFactory`. Configure the named HTTP client with suitable timeouts and **no automatic retries or hedging for generation POST requests**. If using the standard resilience handler, disable unsafe-method retries and hedging.

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Volcengine;
using System.Text.Json.Nodes;

// cache, sharedSessionLock and httpFactory are supplied dependencies.
var provider = new VolcengineLlmProvider(httpFactory);

await using var loki = new LokiClientBuilder()
    .AddLLM(new LlmOptions
    {
        SessionStore = new DistributedCacheLlmSessionStore(
            cache, sharedSessionLock, keyPrefix: "MyApp:Loki:LLM:"),
        Providers = [provider],
        Credentials = [new LlmCredential("ark-key-1", arkApiKey)],
        Routes =
        [
            new LlmRoute
            {
                Model = "assistant",
                Targets =
                [
                    new LlmTarget
                    {
                        Id = "ark-primary",
                        ProviderId = provider.Id,
                        BaseAddress = new Uri("https://ark.cn-beijing.volces.com/api/v3/"),
                        UpstreamModel = arkModelOrEndpoint,
                        CredentialId = "ark-key-1"
                    }
                ]
            }
        ]
    })
    .Build();

var call = new LlmCallOptions { SessionId = "conversation-001" };
var response = await loki.LLM.Responses.CreateAsync(
    new ResponseRequest
    {
        Model = "assistant",
        Input = JsonValue.Create("Hello")
    }, call, cancellationToken);

// Continue on exactly the same upstream target.
await foreach (var item in loki.LLM.Responses.StreamAsync(
    new ResponseRequest
    {
        Model = "assistant",
        PreviousResponseId = response.Id,
        Input = JsonValue.Create("Continue")
    }, call, cancellationToken))
{
    Console.WriteLine(item.Data.GetRawText());
}
```

`loki.LLM.ChatCompletions.CompleteAsync(...)` and `.StreamAsync(...)` use `ChatCompletionRequest`, whose `Messages` is an unmodified JSON array. Both text protocols expose an extension parameter dictionary for fields such as tools, reasoning and caching. `stream` is controlled by the selected method.

A route may contain any valid combination of server, actual model, credential and provider. New Sessions use the configured selector; existing Sessions always reuse the stored complete binding. Switching protocol or public model within a Session is rejected.

Custom LLM providers can explicitly support `LlmCredential.Anonymous("local-instance")` through `ILlmCredentialSupport`. It retains a stable credential identity in the complete Session binding; a missing Key never selects this mode automatically. Existing providers without that optional contract remain API-Key-only. The separate [Ollama LLM provider](src/Asgard.Loki.LLM.Ollama/README.md) now implements native Chat/generate JSON/NDJSON and explicit header isolation; cloud token refresh/signing remains pending; see the [LLM credential contract](src/Asgard.Loki.LLM/README.md#explicit-credential-kinds).

### Native Responses compaction

For a native OpenAI target, explicitly configure `Protocols = LlmProtocol.Responses` and `Features = LlmTargetFeatures.ResponsesCompaction`. Both the target flag and an `IResponsesCompactionAdapter` are required. Ordinary Responses support does not imply compact support; Ark and converting providers do not currently advertise this operation.

Given an OpenAI route named `openai-assistant` with that feature enabled:

```csharp
var compactCall = new LlmCallOptions { SessionId = "openai-context-001" };
var compacted = await loki.LLM.Responses.CompactAsync(
    new ResponseCompactionRequest
    {
        Model = "openai-assistant",
        Input = new JsonArray(new JsonObject
        {
            ["role"] = "user", ["content"] = "The complete context to compact"
        })
    }, compactCall, cancellationToken);

// Reuse the complete returned window, including retained and encrypted items.
var continued = await loki.LLM.Responses.CreateAsync(new ResponseRequest
{
    Model = "openai-assistant",
    Input = JsonNode.Parse(compacted.Output.GetRawText())
}, compactCall, cancellationToken);
```

Compaction is one explicit non-streaming request to `/responses/compact`; the SDK does not trigger it automatically or prune/decrypt the returned window. The optional `PreviousResponseId` uses the same Session ownership checks as creation. A compaction result ID is not registered as a generated response ID for continuation. Calls remain bound to the complete original target, and unsupported features never cause rerouting or retries. Enabling/changing a target feature changes its binding fingerprint; default `Features.None` preserves existing basic-target serialization. See the [OpenAI provider guide](src/Asgard.Loki.LLM.OpenAI/README.md).

### Native Responses WebSocket

Enable `LlmTargetFeatures.ResponsesWebSocket` on a native OpenAI Responses target, then call `loki.LLM.Responses.OpenWebSocketAsync(publicModel, callOptions, cancellationToken)`. The caller owns the connection, sends native JSON and reads all relevant lane/terminal events. Native `stream_id`, tools, opaque state, unknown fields and errors are retained. Session and response ownership stay bound to the original complete target. No reconnect, retry, tool execution or history replay is performed. Initialization, custom factory/resource limits and a complete usage example are in the [OpenAI provider guide](src/Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket). This support is verified offline with in-memory sockets; no live OpenAI verification has been performed.

## Session storage

`DistributedCacheLlmSessionStore` depends on Microsoft's `IDistributedCache`; Redis is one implementation. It does not create or own the cache connection.

First binding requires explicit `ILlmSessionLock` / `ILlmSessionLease` coordination. For multiple processes, that implementation must coordinate through the same distributed system and signal lease loss. Plain cache Get/Set is not atomic. An existing distributed-lock service can be adapted to this small contract without referencing the rest of its framework.

Bindings store a target/configuration fingerprint, never the API Key. Original response IDs are associated with their Session. Missing, expired, foreign or changed bindings fail explicitly; there is no silent provider/model/key fallback. Active bindings use sliding expiration, defaulting to 24 hours.

Redis preserves routing identity, not TCP connections or provider prompt caches. Unknown execution outcomes are not automatically retried. The caller owns Session authorization and isolation, and should choose an application-specific cache prefix.

## Volcengine speech

```csharp
using Asgard.Loki;
using Asgard.Loki.Audio;
using Asgard.Loki.Audio.Volcengine;

var speech = new VolcengineSpeechAdapter(httpFactory, new VolcengineSpeechOptions
{
    ResourceId = speechResourceId,
    ApiKey = speechApiKey
});

await using var loki = new LokiClientBuilder()
    .AddAudio(new AudioOptions
    {
        SpeechRoutes = [new SpeechRoute { Model = "narration", Targets = [speech] }]
    })
    .Build();

var audio = await loki.Audio.Speech.GenerateAsync(new SpeechRequest
{
    Model = "narration",
    Text = "Hello from Loki.",
    Voice = authorizedVoiceId,
    Format = "mp3",
    SampleRate = 24000
}, cancellationToken);
```

Use `.StreamAsync(...)` for ordered audio and metadata chunks. The adapter decodes each Base64 frame independently and requires the upstream completion code. Subtitles, usage and unknown fields remain available. An Ark LLM key is not assumed to authorize speech; configure speech credentials and resource IDs independently. Legacy authentication uses `AppId` plus `AccessKey` instead of `ApiKey`.

## OpenAI images

```csharp
using Asgard.Loki;
using Asgard.Loki.Image;
using Asgard.Loki.Image.OpenAI;
using System.Text.Json;

var images = new OpenAiImageAdapter(httpFactory, new OpenAiImageOptions
{
    ApiKey = openAiApiKey,
    Model = actualImageModel
});

await using var loki = new LokiClientBuilder()
    .AddImage(new ImageOptions
    {
        Routes = [new ImageRoute { Model = "pictures", Targets = [images] }]
    })
    .Build();

var result = await loki.Image.GenerateAsync(new ImageGenerationRequest
{
    Model = "pictures",
    Prompt = "A watercolor lighthouse at dawn",
    Parameters = new()
    {
        ["background"] = JsonSerializer.SerializeToElement("transparent"),
        ["output_format"] = JsonSerializer.SerializeToElement("png")
    }
}, cancellationToken);

// For Base64 results; URL-only results remain available through .Url.
ReadOnlyMemory<byte> imageBytes = result.Images[0].Decode();
```

Use `loki.Image.StreamAsync(...)` for partial/final image events when supported by the configured upstream model. Image count, size, quality, compression, transparency and other parameters are passed through unchanged. Provider URLs are returned without downloading them. The library does not rewrite prompts or infer model availability.

## Build and test

```powershell
dotnet restore Asgard.Loki.slnx
dotnet build Asgard.Loki.slnx -c Release --no-restore
pwsh -File eng/Test.ps1
dotnet pack Asgard.Loki.slnx -c Release --no-build -o artifacts/packages
pwsh -File eng/Verify-PackageBoundaries.ps1
```

Tests use xUnit v3 / Microsoft.Testing.Platform, `MemoryDistributedCache`, an in-process test coordinator and stub HTTP handlers. They require no provider keys, network or Redis. The test script allows the no-tests exit code only for the reserved Video test project; all other test projects must discover tests. Remove that allowance when Video tests are implemented.

Local protocol tests are not live-provider verification. Real account permissions, model availability and Redis cross-process coordination require separate integration checks. No real provider calls or package publication are performed by the unit tests.

The [test configuration contract](docs/testing.md) and [.env.example](.env.example) define exactly four variables for the separate Volcengine LLM live suite: opt-in, API Key, Chat model and Responses model. Run it explicitly with `pwsh -File eng/Test-Live.ps1`. The script supports Windows current-user variables and does not load `.env` files. Ordinary tests and automatic CI stay offline even when live credentials exist.

The [Anthropic native transport](src/Asgard.Loki.LLM.Anthropic/README.md#explicit-authentication-and-http-policy) now has explicit Key/Bearer selection, bounded terminal handling and separate standard/Runtime/Mantle Messages fixtures. Predecessor Anthropic Sessions need new IDs or expiry. [Conversion settings coverage](src/Asgard.Loki.LLM.Conversion/README.md#explicit-settings-through-a-fixed-native-messages-transport) checks reasoning on/off, complete Schema constraints, explicit loss diagnostics and late errors through that fixed native transport. All coverage is offline. Native Mantle SigV4 JSON/SSE now uses the separate [AWS Mantle adapter](src/Asgard.Loki.LLM.AWS/README.md#explicit-mantle-sigv4-messages); Native Nova InvokeModel JSON and binary EventStream now use `.LLM.BedrockNova`; explicit Nova ↔ Converse complete JSON conversion is now available separately; common Nova/Converse logical text/tool streams are also available; mixed four-protocol/cloud variants remain pending.

The offline suite passes 6483 tests, including 722 native AWS Runtime/Mantle provider tests, 451 native Vertex JSON/SSE, 155 native Azure JSON/SSE, 167 native Ollama Chat/generate, 143 SiliconFlow Rerank, 350 OpenAI Image, 96 Image core, 378 OpenAI Audio, 122 Audio core, 134 Jina Embedding, 124 Ali Rerank, 125 Ollama Embedding, 133 Jina Rerank, 120 Cohere Rerank, 34 Rerank core, 34 Embedding core, 106 OpenAI Embedding, 114 Google Embedding, 2217 conversion, 284 OpenAI provider, 93 Ark provider, 127 Claude, 36 Gemini and 208 LLM core tests. Complete Chat/Responses URL citations and Gemini Web grounding map in both directions with declared endpoint index units. Conversion regressions cover nested field loss, pending tool/result association, ID collisions, complete and truncated stream lifecycles, done/final snapshot consistency and cache/usage accounting. The original OpenAI/Ark protocol suites remain covered alongside native compaction and feature-binding tests. Standard-Ark live verification with `doubao-seed-evolving` covers Chat/Responses JSON and SSE, tool calls and result return, strict JSON Schema, thinking on/off, actual output truncation, response continuation and opaque reasoning replay. **Both strict `tool_choice=none` cases currently fail:** upstream emits a function call although the final request correctly contains `none`. These failures remain visible. See the [protocol and parameter verification record](docs/llm-parameter-verification.md) for evidence and limits; OpenAI, Claude, Gemini, Embedding/Rerank suppliers and explicit conversion have offline coverage only.

Use `pwsh -File eng/Test-Live.ps1 -Suite Parameters` or `-Suite All` for expanded checks, or add `-Scenario Structured`, `Tools`, `ToolChoice`, `Reasoning`, `Limits` or `Replay` to select a group. Default smoke makes five calls; Parameters plans 26 and All 31, with a hard limit of 32 per process. The expanded suites exit nonzero while the known tool-choice failure persists. The fixture uses `MemoryDistributedCache`; Redis cross-process verification remains separate.

Typed Claude/Responses stream errors now use their native event discriminator. Unknown typed extension events and `error` extension fields retain strict loss diagnostics; explicit loss consent can convert the known content without misclassifying business metadata as an upstream failure. Independent complete source/target snapshots and 56 offline regressions verify original tool IDs/arguments, exact integers and failure-safe termination through the public SDK. See the [conversion boundary](src/Asgard.Loki.LLM.Conversion/README.md#typed-source-stream-errors).

The existing conversion package now also offers [explicit Nova ↔ Converse complete JSON mapping](src/Asgard.Loki.LLM.Conversion/README.md#explicit-nova--converse-complete-json) through `ConvertingBedrockLlmProvider`. Callers select the native Nova envelope and Converse model-setting layout. Original ordinary tool IDs/results, media containers, JSON Schema and explicit reasoning intent remain intact. This separate native pair now also supports logical text/tool streams while preserving native indices and waiting for normal source completion; it does not generate target binary frames or enable mixed mappings to the four text protocols.

The [native pair stream guide](src/Asgard.Loki.LLM.Conversion/README.md#public-sdk-native-stream-composition) covers the dedicated data converter, public SDK composition, finite tool/tail/diagnostic state and raw-versus-logical event metadata. Independent 15-event goldens plus 153 conversion and 58 AWS regressions verify strict loss, tool/history association, terminal withholding and source read/disposal/cancellation faults offline.

Caller-owned LLM adapters may now [declare their protocol scope](src/Asgard.Loki.LLM/README.md#optional-protocol-support-declaration) in addition to implementing adapter interfaces. Configuration validates every target, including backups, before invocation. Legacy adapters keep interface-based support; this prerequisite does not expand any conversion pair or model capability.

## Contributing and releases

The [model-call roadmap](docs/model-call-roadmap.md#remaining-sdk-work-at-the-current-checkpoint) was rechecked on 2026-10-19 against unchanged New API `058c8231` and Loki `5403f1d`. Native Claude/Gemini, documented four-protocol conversion, OpenAI compaction/Responses WebSocket, OpenAI/Google/Ollama/Jina Embedding (documented native subset) and the independent Rerank core are implemented. Native Cohere v1/v2, Jina text/multimodal/listwise, DashScope nested text/media and [SiliconFlow scalar/text/image Rerank](src/Asgard.Loki.Rerank.SiliconFlow/README.md) are implemented with independent offline fixtures. See the [Cohere](src/Asgard.Loki.Rerank.Cohere/README.md) and [Jina](src/Asgard.Loki.Rerank.Jina/README.md) initialization guides. The next checkpoint closes the bounded conversion field/mode inventory before remaining supplier dialects. Converted Responses annotation streams now validate original indices, Unicode ranges, raw done/final consistency and bounded state even with explicit citation loss consent. Gemini source-stream grounding now validates cumulative source indices, bounded state and metadata-only tails even with loss consent. Explicit single-text-part Responses ↔ Gemini citation streams emit validated native annotations or grounding only after normal source completion; Responses done/final snapshots stay consistent and Gemini uses original cumulative byte ranges/source order. Explicit ConcatenatedTextParts additionally supports ordered pure-text Responses parts/messages, without changing the existing SingleTextPart boundary or permitting tool/text interleaving. Declared UTF-8 text files additionally retain document containers across Responses/Claude/Gemini; Chat non-PDF files remain an explicit boundary. Other citation-stream layouts/pairs and remaining search/media/result/error/output mappings remain pending. Native [Ollama Embedding](src/Asgard.Loki.Embedding.Ollama/README.md) now has independent text/media/empty-input and ordered-vector fixtures. [DashScope Rerank](src/Asgard.Loki.Rerank.Ali/README.md) now has native nested request/result and original-document association fixtures. The [Jina compatibility audit](src/Asgard.Loki.Embedding.OpenAI/README.md#jina-compatibility-audit) verifies the reusable text/dense subset and keeps OpenAI-provider boundaries explicit. The independent [native Jina package](src/Asgard.Loki.Embedding.Jina/README.md) now adds text/media/fused inputs and Float/Base64 single/per-token output; PDF association and sparse/packed/multiple formats remain separate work. Other Rerank dialects also remain explicit supplier gaps; existing providers do not establish all-supplier coverage.

Remaining checkpoints add other Image/Audio supplier operations, cloud/native supplier differences, Realtime and Video, with legacy completion, moderation, discovery and independently callable search/fetch kept in the lower-priority inventory. Each implementation carries independent protocol fixtures, local public SDK tests and package/architecture documentation in small local commits. Windows/Linux GitHub CI runs at the PR and explicit release gates; any authorized push targets `dev`. Native transport, explicit conversion and live model acceptance are separate claims. Administration, billing, proxy hosting, tool execution and background orchestration are excluded.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and validation rules, [SECURITY.md](SECURITY.md) for reporting vulnerabilities, and [docs/releasing.md](docs/releasing.md) for the package release checklist. Development checkpoints use local validation. GitHub CI runs on Windows/Linux only for PRs into `main` or reuse by the explicit NuGet release workflow; ordinary pushes and standalone manual CI runs are disabled. Dependabot checks NuGet dependencies and GitHub Actions weekly.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).

The Conversion data API now supports the common complete-call subset of four text requests to native Converse and context-bound replies back, with independent full JSON goldens. Tool IDs, JSON/text result kinds and schema constraints are retained. Public SDK composition is available for fixed Converse; inverse operations, mixed streams and model-specific reasoning remain separate work; see the [conversion contract](src/Asgard.Loki.LLM.Conversion/README.md#four-text-requests-to-native-converse-complete-data-conversion).

The existing Bedrock conversion decorator now composes those complete text calls through the public SDK when fixed to Converse. Fixed Nova stays limited to the native pair; mixed text streams fail before credential/HTTP acquisition. See [SDK initialization and limits](src/Asgard.Loki.LLM.Conversion/README.md#public-sdk-text-calls-over-native-converse).

Converted Responses output IDs remain visible but no longer enter native response-chain ownership. Complete/stream SDK clients preserve native continuation behavior and reject a new converted-ID continuation before another upstream call; see the [ownership contract](src/Asgard.Loki.LLM/README.md#native-responses-ownership-versus-converted-output-ids).

Converse conversion now has explicit Anthropic/Nova2 **request-setting** dialects for reasoning and structured output, backed by independent full JSON and public SDK HTTP tests. Explicit Claude/Converse Anthropic signed response/history data conversion is now available; other reasoning round trips remain unfinished and fail without discarding data. See the [settings matrix and limits](src/Asgard.Loki.LLM.Conversion/README.md#explicit-converse-model-reasoning-request-settings).

The [Claude/Converse Anthropic complete-data codec](src/Asgard.Loki.LLM.Conversion/README.md#claude--converse-anthropic-signed-complete-data) now preserves signed text, redacted bytes and full historical block/message order under an explicit dialect. It keeps strict boundaries for other protocol/model families and does not verify signatures or claim cross-model portability.

The [signed replay SDK checkpoint](src/Asgard.Loki.LLM.Conversion/README.md#public-sdk-claude--converse-signed-historical-replay) now verifies two actual public calls over substitute native HTTP: first-response content is replayed unchanged with the full previous history and existing target/session binding. Native resource faults and undeclared signature projections fail without success or fallback.
