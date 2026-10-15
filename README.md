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
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions; Responses creation and continuation | SSE |
| Speech | `Asgard.Loki.Audio.Volcengine` | Volcengine V3 text-to-speech; API Key or legacy app/token authentication | HTTP chunked JSON to audio chunks |
| Images | `Asgard.Loki.Image.OpenAI` | OpenAI Images Generations; Base64 and URL results | SSE partial/final image events |
| Video | — | Project scaffold only | — |

Requests preserve unknown JSON parameters. Results retain complete upstream JSON, including tools, reasoning, usage, errors and future events. Public model aliases and actual upstream models are exposed separately.

Native Responses WebSocket transport, image edits/variations, speech recognition, music generation, video generation, billing and automatic request replay are **not implemented**. Supported model names and account entitlements are configured by the caller and validated by the actual provider; there is no hard-coded model catalog.

## Packages and dependencies

| Package | Responsibility |
| --- | --- |
| `Asgard.Loki.Abstractions` | Generic client and module builder contracts |
| `Asgard.Loki` | Client implementation and module lifecycle |
| `Asgard.Loki.LLM` | Chat/Responses contracts, routing, Session storage and registration |
| `Asgard.Loki.Audio` | Independent speech contracts, routing and audio aggregation |
| `Asgard.Loki.Image` | Independent image contracts, routing and results |
| `Asgard.Loki.Video` | Reserved video capability scaffold |
| `Asgard.Loki.LLM.Volcengine` | Ark LLM provider |
| `Asgard.Loki.LLM.OpenAI` | OpenAI LLM provider |
| `Asgard.Loki.Audio.Volcengine` | Volcengine speech provider |
| `Asgard.Loki.Image.OpenAI` | OpenAI image provider |

The entry package depends only on abstractions. Capability packages do not reference each other. Each provider depends only on its capability and standard Microsoft components. Installing OpenAI images does not install LLM or audio providers.

These are the package identities produced by this checkout. CI builds and verifies all ten packages and attaches them to the workflow run. The separate manual `Publish NuGet` workflow supports OIDC login verification and explicit publication; its default `verify` mode does not upload packages. See [release instructions](docs/releasing.md). Local preview packages can be created with `dotnet pack`.

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

Live smoke verification with `doubao-seed-evolving` on the standard Ark API has passed for Chat JSON/streaming, Responses JSON/streaming and cross-client response continuation: five actual HTTPS requests. This verifies the covered scenarios for the configured account/model, not every parameter or other providers. Session storage in this fixture uses `MemoryDistributedCache`; Redis cross-process verification remains separate.

## Contributing and releases

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and validation rules, [SECURITY.md](SECURITY.md) for reporting vulnerabilities, and [docs/releasing.md](docs/releasing.md) for the package release checklist. CI runs on Windows and Linux, and Dependabot checks NuGet dependencies and GitHub Actions weekly.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).
