# Asgard.Loki

[![CI](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml/badge.svg)](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

A modular .NET 10 / C# 14 client library for model APIs. Use one `LokiClient` and install only the capabilities and providers you need.

The architecture contract, UML, dependency rules and provider extension requirements live in [docs/architecture.md](docs/architecture.md). All namespaces start with `Asgard`.

## Supported capabilities

| Capability | Provider package | Implemented APIs | Streaming |
| --- | --- | --- | --- |
| LLM | `Asgard.Loki.LLM.Volcengine` | Ark Chat Completions; Responses creation and continuation | SSE |
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions; Responses creation/continuation; explicit native compaction and WebSocket | SSE; JSON compaction; native WebSocket events |
| LLM | `Asgard.Loki.LLM.Anthropic` | Native Claude Messages | SSE |
| LLM | `Asgard.Loki.LLM.Google` | Native Gemini generateContent | SSE |
| LLM conversion | `Asgard.Loki.LLM.Conversion` | Explicit common-subset request/response conversion between Chat, Responses, Claude and Gemini | Text deltas and complete function calls |
| Speech | `Asgard.Loki.Audio.Volcengine` | Volcengine V3 text-to-speech; API Key or legacy app/token authentication | HTTP chunked JSON to audio chunks |
| Images | `Asgard.Loki.Image.OpenAI` | OpenAI Images Generations; Base64 and URL results | SSE partial/final image events |
| Video | — | Project scaffold only | — |

Native requests preserve unknown JSON parameters. Native results retain complete upstream JSON, including tools, reasoning, usage, errors and future events. Public model aliases and actual upstream models are exposed separately. Optional conversion requires explicit registration, rejects unsupported fields by default, and retains upstream JSON and diagnostics separately from the converted result. User inline PDF inputs now map across the four protocols; native filenames, private references and metadata have explicit preservation/loss boundaries, with no SDK file download. Complete Chat/Responses URL citations map with unchanged-text ranges or safe ASCII-prefix rebasing; ambiguous Unicode offsets and citation stream/history mappings remain explicit boundaries. See the [conversion support matrix](src/Asgard.Loki.LLM.Conversion/README.md).

Image edits/variations, speech recognition, music generation, video generation and automatic request replay are **not implemented**. Gateway administration and billing are outside this SDK. Supported model names and account entitlements are configured by the caller and validated by the actual provider; there is no hard-coded model catalog.

## Packages and dependencies

| Package | Responsibility |
| --- | --- |
| `Asgard.Loki.Abstractions` | Generic client and module builder contracts |
| `Asgard.Loki` | Client implementation and module lifecycle |
| `Asgard.Loki.LLM` | Four native text protocols, routing, Session storage and registration |
| `Asgard.Loki.Audio` | Independent speech contracts, routing and audio aggregation |
| `Asgard.Loki.Image` | Independent image contracts, routing and results |
| `Asgard.Loki.Video` | Reserved video capability scaffold |
| `Asgard.Loki.LLM.Volcengine` | Ark LLM provider |
| `Asgard.Loki.LLM.OpenAI` | OpenAI LLM provider |
| `Asgard.Loki.LLM.Anthropic` | Native Claude Messages provider |
| `Asgard.Loki.LLM.Google` | Native Gemini Developer API provider |
| `Asgard.Loki.LLM.Conversion` | Optional explicit conversion; references only LLM |
| `Asgard.Loki.Audio.Volcengine` | Volcengine speech provider |
| `Asgard.Loki.Image.OpenAI` | OpenAI image provider |

The entry package depends only on abstractions. Capability packages do not reference each other. Each provider depends only on its capability and standard Microsoft components. Installing OpenAI images does not install LLM or audio providers.

These are the package identities produced by this checkout. CI builds and verifies all thirteen packages and attaches them to the workflow run. The separate manual `Publish NuGet` workflow supports OIDC login verification and explicit publication; its default `verify` mode does not upload packages. See [release instructions](docs/releasing.md). Local preview packages can be created with `dotnet pack`.

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

The offline suite passes 1551 tests, including 1118 conversion, 199 OpenAI provider, 93 Ark provider, 22 Claude, 36 Gemini and 65 LLM core tests. Conversion regressions cover nested field loss, pending tool/result association, ID collisions, complete and truncated stream lifecycles, done/final snapshot consistency and cache/usage accounting. The original OpenAI/Ark protocol suites remain covered alongside native compaction and feature-binding tests. Standard-Ark live verification with `doubao-seed-evolving` covers Chat/Responses JSON and SSE, tool calls and result return, strict JSON Schema, thinking on/off, actual output truncation, response continuation and opaque reasoning replay. **Both strict `tool_choice=none` cases currently fail:** upstream emits a function call although the final request correctly contains `none`. These failures remain visible. See the [protocol and parameter verification record](docs/llm-parameter-verification.md) for evidence and limits; OpenAI, Claude, Gemini and explicit conversion have offline coverage only.

Use `pwsh -File eng/Test-Live.ps1 -Suite Parameters` or `-Suite All` for expanded checks, or add `-Scenario Structured`, `Tools`, `ToolChoice`, `Reasoning`, `Limits` or `Replay` to select a group. Default smoke makes five calls; Parameters plans 26 and All 31, with a hard limit of 32 per process. The expanded suites exit nonzero while the known tool-choice failure persists. The fixture uses `MemoryDistributedCache`; Redis cross-process verification remains separate.

## Contributing and releases

The [model-call roadmap](docs/model-call-roadmap.md) was rechecked on 2026-10-19 against unchanged New API `058c8231`, using Loki `4be4511` and its successful Windows/Linux CI as the comparison anchor. Native Claude/Gemini, reasoning/schema settings, custom/namespace tool identity, OpenAI compaction and native Responses WebSocket are implemented for their documented scope. Hosted search covers declared request configuration and diagnostics; complete Chat/Responses URL citations are now delivered separately. Broader search results/grounding, representable citation streams and remaining controls stay pending. User inline PDF inputs are delivered; other documents/media, rich/error tool results and representable output shapes remain pending. Independent Embedding/Rerank packages follow, then image edits, audio, cloud suppliers, Realtime and Video. Each checkpoint requires pair-specific fixtures, public SDK tests and documentation. Native transmission, explicit conversion and live supplier verification are assessed separately. Gateway administration, billing, tool execution and task hosting remain outside this SDK.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and validation rules, [SECURITY.md](SECURITY.md) for reporting vulnerabilities, and [docs/releasing.md](docs/releasing.md) for the package release checklist. CI runs on Windows and Linux, and Dependabot checks NuGet dependencies and GitHub Actions weekly.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
