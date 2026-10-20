# Asgard.Loki

[![CI](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml/badge.svg)](https://github.com/BenLampson/Asgard.Loki/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

A modular .NET 10 / C# 14 client library for model APIs. Use one `LokiClient` and install only the capabilities and providers you need.

The SDK scope is six capability families: **LLM, Audio, Video, Image, Embedding and Rerank**. Embedding and Rerank retain independent capability/provider packages. Fine-tuning, independent provider file management, gateway administration and billing are outside this scope.

The architecture contract, UML, dependency rules and provider extension requirements live in [docs/architecture.md](docs/architecture.md). All namespaces start with `Asgard`.

Native protocols preserve original parameters, results and unknown fields. Explicit [protocol conversion](src/Asgard.Loki.LLM.Conversion/README.md) supports its documented reliable subset, including bounded opt-in Chat/Gemini candidate streams; unsupported mappings fail explicitly. [Realtime](src/Asgard.Loki.LLM.OpenAI/README.md#native-realtime-websocket) is caller-driven. [Video](src/Asgard.Loki.Video/README.md) provides independent native task contracts and persistent complete-target references; the [Ark](src/Asgard.Loki.Video.Volcengine/README.md), [DashScope](src/Asgard.Loki.Video.Ali/README.md) and [Gemini Veo](src/Asgard.Loki.Video.Google/README.md) providers submit and query native video generation tasks. Video snapshots optionally retain raw HTTP metadata and explicitly distinguish an empty HTTP 202 from native JSON; see the [result contract](src/Asgard.Loki.Video/README.md).


Optional [native Audio task contracts](src/Asgard.Loki.Audio/README.md#native-audio-tasks) provide explicit submission, bound status queries, native batch queries and result-only retrieval through application-owned adapters. Missing batch tasks remain explicit; media presence never implies completion. The core supplies no native music protocol by itself.

## Supported capabilities

Fine-tuning jobs and general provider file management (upload/list/retrieve/download/delete as independent operations) are **not implemented**. Existing audio/image uploads supply inputs to transcription, translation or image editing requests.

| Capability | Provider package | Implemented APIs | Streaming |
| --- | --- | --- | --- |
| LLM | `Asgard.Loki.LLM.Volcengine` | Ark Chat Completions; Responses creation and continuation | SSE |
| LLM | `Asgard.Loki.LLM.OpenAI` | OpenAI Chat Completions; Responses creation/continuation; native Moderations; explicit compaction, Responses WebSocket and independent Realtime | SSE; JSON compaction; native JSON WebSocket events |
| LLM | `Asgard.Loki.LLM.Anthropic` | Native Claude Messages | SSE |
| LLM | `Asgard.Loki.LLM.Google` | Native Gemini generateContent | SSE |
| LLM | `Asgard.Loki.LLM.Ollama` | Native Chat/generate JSON/NDJSON, native thinking/schema/tool history and prompt/context; explicit anonymous or Bearer identity | — |
| LLM | `Asgard.Loki.LLM.Azure` | Native v1/dated Chat, Responses and text Completions JSON; explicit native Responses compaction and independent Realtime; api-key or borrowed external tokens | Native SSE with delayed filter/usage tails; native Realtime JSON WebSocket |
| LLM | `Asgard.Loki.LLM.DeepSeek` | Native Search / Fetch JSON; explicit fixed service credentials; complete ranked results and page content | — |
| LLM | `Asgard.Loki.LLM.Tencent` | Native Hunyuan complete JSON; TC3 and caller-owned External principal credentials | Native SSE, unchanged tools and all-choice terminal/metadata tails |
| LLM | `Asgard.Loki.LLM.Xunfei` | Native Spark signed WebSocket; complete ordered native message window | Original JSON messages; text/global terminal and normal-close validation |
| LLM | `Asgard.Loki.LLM.Cohere` | Native v1 Chat JSON; full history, references and call-plus-parameters tool results | Native NDJSON; unchanged tool fragments and delayed stream-end |
| LLM | `Asgard.Loki.LLM.Cloudflare` | Explicit native ai/run text JSON; full native envelope and tools | Original SSE JSON and actual DONE control frame; delayed terminal |
| LLM | `Asgard.Loki.LLM.Coze` | Native v3 bot chat acknowledgement, explicit status/messages queries; complete-target references | Native two-level Chat/Message SSE, raw future events and actual done |
| LLM | `Asgard.Loki.LLM.Dify` | Native app-key chat JSON; explicit Chatbot/Agent/Chatflow profiles | Original SSE with complete workflow/TTS tails and delayed terminal |
| LLM | `Asgard.Loki.LLM.GoogleVertex` | Vertex Gemini/Claude JSON/SSE with borrowed tokens; separate Express Gemini JSON/SSE with explicit API Key; MaaS Chat JSON/SSE with borrowed tokens | Native events with delayed terminal checks |
| LLM | `Asgard.Loki.LLM.AWS` | Native Bedrock Claude and explicit Converse JSON/EventStream with borrowed SigV4 or explicit Bedrock API Key credentials | Native binary EventStream with independent protocol lifecycles |
| LLM conversion | `Asgard.Loki.LLM.Conversion` | Explicit common-subset request/response conversion between Chat, Responses, Claude and Gemini | Text deltas and complete function calls |
| Speech | `Asgard.Loki.Audio.Volcengine` | V3 TTS plus explicit V1 HTTP query / binary WebSocket submit; native JSON and metadata | HTTP JSON or binary WebSocket to ordered audio chunks |
| Speech | `Asgard.Loki.Audio.OpenAI` | Native Speech binary/SSE responses; custom voice objects and complete native JSON settings | Incremental binary bytes or native delta/done events |
| Compatible audio profiles | `Asgard.Loki.Audio.OpenAI` | Explicit SiliconFlow voice-based binary speech and multipart transcription; original parameters/results | Existing adapters with verified roots/auth/wire; no brand-only package |
| Speech | `Asgard.Loki.Audio.MiniMax` | Native synchronous `t2a_v2`; original parameters, hex audio or unchanged URL results | One complete JSON result; no implicit URL download |
| Audio recognition / translation | `Asgard.Loki.Audio.Cloudflare` | Explicit native raw-binary recognition or JSON/Base64 task transcribe/translate; complete original envelopes | Borrowed single-use uploads; no multipart inference or implicit profile switch |
| Azure audio | `Asgard.Loki.Audio.Azure` | Explicit deployment/API-version/api-key speech JSON and native multipart transcription/translation | Ordered opaque audio or complete original file results; optional fixed body model |
| Music / lyrics tasks | `Asgard.Loki.Audio.SunoAPI` | Explicit native MUSIC/LYRICS submit, status/batch POST and separate raw result GET on an application-configured proxy | One-shot native JSON; no background polling or media download |
| File transcription / translation | `Asgard.Loki.Audio.OpenAI` | Native multipart, complete JSON/verbose/diarized or text/subtitle results | Native transcription delta/segment/done SSE |
| Images | `Asgard.Loki.Image.OpenAI` | OpenAI image generations and native JSON/multipart edits; Base64 and URL results | Native generation/edit SSE partial/final events |
| Compatible image generation | `Asgard.Loki.Image.OpenAI` | Explicit xAI native complete JSON profile with independent root/model/key and original data[] results | Verified offline; xAI editing uses a distinct native JSON contract |
| Image editing | `Asgard.Loki.Image.XAI` | Native xAI JSON image.url or ordered images[].url/file_id references; complete original results | Separate edit routes; no multipart conversion or file management |
| Image | `Asgard.Loki.Image.Midjourney` | Explicit unofficial proxy image operations, original task/text outcomes, input upload and seed requests | Bound native task references and single/batch queries; no synchronous-result fabrication |
| Image | `Asgard.Loki.Image.Replicate` | Native model predictions/JSON editing and explicit model-input upload; original prediction states/results | Caller-owned snapshots, no implicit polling/downloads; complete-result guard |
| Image | `Asgard.Loki.Image.Zhipu` | Native v4 images/generations; original URL/Base64 aliases, content filtering, timestamps and metadata | Complete native JSON; no URL download or invented metadata |
| Image | `Asgard.Loki.Image.Azure` | Native dated deployment-scoped JSON generation and multipart editing; complete original images/filtering/usage | Native generation/editing SSE; original image events, no synthetic streams |
| Image | `Asgard.Loki.Image.Volcengine` | Native Ark generation/image inputs and omitted-prompt layers; independently signed Jimeng Visual CVProcess JSON | Native partial successes/failures and original summary SSE; single-image EOF |
| Image | `Asgard.Loki.Image.Ali` | Native multimodal JSON / interleaved SSE; explicit modern and legacy image task submission | Caller-driven task GET; original text/image/usage/failures |
| Image | `Asgard.Loki.Image.Google` | Native Gemini image generation/editing inputs; preserved inline images, signed parts and safety outcomes | Original native SSE and candidate terminal/usage metadata |
| Image | `Asgard.Loki.Image.GoogleVertex` | Explicit Vertex project/Express resources and Bearer/API-Key authentication; original image parts and safety outcomes | Native Gemini JSON/SSE; terminal and usage preservation |
| Image | `Asgard.Loki.Image.MiniMax` | Native image_generation JSON and subject-reference inputs; original URL/Base64 arrays and metadata | Complete result; native business errors retained |
| Image | `Asgard.Loki.Image.SiliconFlow` | Native image_size/batch_size/reference fields and images/timings/seed result envelope | Complete original JSON; no implicit OpenAI field conversion |
| Embedding | `Asgard.Loki.Embedding.Azure` | Native deployment-scoped embeddings with explicit API version/api-key, original text/token inputs and float/Base64 vectors | Original batch order and index coverage; complete errors and bounded HTTP ownership |
| Embedding | `Asgard.Loki.Embedding.OpenAI` | Native Embeddings, text/token batch inputs, Float/Base64 and indexed results | Explicit compatible text profiles, including M3E/Workers AI; bounded raw errors and final cancellation |
| Embedding | `Asgard.Loki.Embedding.Google` | Native embedContent / synchronous batchEmbedContents, native settings/media JSON and ordered vectors | — |
| Embedding | `Asgard.Loki.Embedding.Ollama` | Native embed text/media batches, original settings, ordered vectors and optional native counters | — |
| Embedding | `Asgard.Loki.Embedding.Jina` | Native text/media/fused groups, indexed dense and per-token Float/Base64 embeddings | — |
| Rerank | `Asgard.Loki.Rerank.Cohere` | Explicit native v1/v2; original document indices/scores and full JSON | — |
| Rerank | `Asgard.Loki.Rerank.Jina` | Native text/multimodal/listwise; original indices, optional document vectors and full JSON | — |
| Rerank | `Asgard.Loki.Rerank.Ali` | Native DashScope nested text/media requests, original indices/documents, scores and usage | — |
| Rerank | `Asgard.Loki.Rerank.SiliconFlow` | Native scalar/text/image requests, original indices/documents, optional native token metadata | — |
| Rerank | `Asgard.Loki.Rerank.SGLang` | Native text/content-part inputs, score/index root arrays, original document echoes/meta_info | — |
| Rerank | `Asgard.Loki.Rerank.OpenAICompatible` | Explicit native results/index/relevance_score wire, original JSON candidates and optional usage | — |
| Video | `Asgard.Loki.Video.Volcengine` | Ark native task submission/query, original media/asset references and result URLs | Caller-driven task snapshots |
| Video | `Asgard.Loki.Video.Volcengine` | Jimeng Visual signed native submit/query, fixed req_key, original fields and optional query settings | Caller-driven task snapshots |
| Video | `Asgard.Loki.Video.Ali` | DashScope native video-generation/image2video submission and task-query results | Caller-driven task snapshots |
| Video | `Asgard.Loki.Video.Google` | Native Gemini Veo predictLongRunning, operation query and explicit output copy; previews retire 2026-10-22 | Caller-driven task snapshots/content |
| Video | `Asgard.Loki.Video.GoogleVertex` | Native Vertex Veo resource-scoped prediction/query, borrowed tokens, GCS and inline results | Caller-driven task snapshots |
| Video | `Asgard.Loki.Video.MiniMax` | Explicit native v1/v2 generation/query and v1 video-result metadata, original media/usage/errors | Caller-driven task snapshots/results |
| Video | `Asgard.Loki.Video.Kling` | Native text2video/image2video submit/query, explicit API Key or AK/SK JWT, original task/video metadata | Caller-driven task snapshots |
| Video | `Asgard.Loki.Video.Vidu` | Four explicit native generation operations and creations query, Token authentication, all original media/metadata | Caller-driven task snapshots |
| Video | `Asgard.Loki.Video.XAI` | Native generation/edit/extension and one-shot query, original media/usage/moderation, explicit empty-202 HTTP snapshots | Caller-driven task snapshots |
| Video | `Asgard.Loki.Video.Midjourney` | Explicit unofficial proxy video generation/extension and one-shot query, original codes, parent indices and all outputs | Caller-driven task snapshots |
| Video media assets | `Asgard.Loki.Video.Volcengine` | Ark native Asset/AssetGroup create/get/list/update/delete with independent AK/SK signing | One explicit native operation or page |

Audio exposes an optional [native JSON speech contract](src/Asgard.Loki.Audio/README.md), with independent snapshots and delayed success until source enumeration/disposal finishes. [OpenAI speech](src/Asgard.Loki.Audio.OpenAI/README.md) implements native binary HTTP and explicit SSE aggregation/streaming, including custom voice objects and preserved audio/event data. Binary frames are SDK HTTP records; SSE envelopes retain native JSON/raw text, event identity and original usage.

Native requests preserve unknown JSON parameters. Native results retain complete upstream JSON, including tools, reasoning, usage, errors and future events. Public model aliases and actual upstream models are exposed separately. Optional conversion requires explicit registration, rejects unsupported fields by default, and retains upstream JSON and diagnostics separately from the converted result. User inline PDF inputs map across four protocols, and explicitly declared UTF-8 text files map between Responses, Claude and Gemini; native filenames, private references and metadata have explicit preservation/loss boundaries, with no SDK file download. Complete Chat/Responses URL citations map with unchanged-text ranges or safe ASCII-prefix rebasing; declared endpoint units now enable exact Unicode offsets and complete Gemini Web grounding ↔ Chat/Responses citations. Responses ↔ Gemini citation streams additionally support an explicit single cumulative text-part convention and declared OpenAI endpoint index unit; a separate opt-in [text-part concatenation](src/Asgard.Loki.LLM.Conversion/README.md#explicit-concatenation-of-responses-text-parts) maps sequential Responses text blocks into that cumulative target with exact citation rebasing; other stream/history pairs retain documented boundaries. See the [conversion support matrix](src/Asgard.Loki.LLM.Conversion/README.md).

Image exposes separate native JSON and caller-owned multipart editing contracts, including OpenAI native event streaming; see the [Image README](src/Asgard.Loki.Image/README.md#native-image-editing-contracts). The bundled OpenAI provider implements native JSON/multipart nonstream and SSE edits; generation and editing now hold native completion through EOF and transport disposal. Image variations, music generation and automatic request replay are **not implemented**. Audio exposes native audio-file transcription/translation contracts and an OpenAI multipart provider; see the [Audio README](src/Asgard.Loki.Audio/README.md#native-file-transcription-and-translation-contracts). Gateway administration and billing are outside this SDK. Supported model names and account entitlements are configured by the caller and validated by the actual provider; there is no hard-coded model catalog.

Explicit Claude/Gemini tool-error history now supports two caller-declared formats: a complete Gemini response JSON object, or original error text. Native failure flags, original call IDs and reverse-order parallel associations stay intact; ordinary success objects cannot be implicitly upgraded by an error key. Default conversion and Chat/Responses error destinations remain strict boundaries. See the [error format matrix](src/Asgard.Loki.LLM.Conversion/README.md#explicit-claudegemini-tool-errors).

Public LLM snapshots reject malformed original Unicode before JSON serialization and Session/cache work, including tool IDs, error text, unknown DOM/extension fields and optional compaction/WebSocket input. Valid Unicode and existing valid Session fingerprints stay unchanged. See the [encoding contract](src/Asgard.Loki.LLM/README.md#native-json-snapshot-encoding).

LLM additionally exposes an explicit [external authentication identity](src/Asgard.Loki.LLM/README.md#external-authentication-identity) for provider-owned token/signing sources. Core Sessions bind the stable principal and configuration, never a rotating access token. Existing API Key/anonymous contracts and fingerprint bytes stay compatible; a dedicated cloud provider must opt in and implement its own transport/authentication.

The independent [Azure LLM provider](src/Asgard.Loki.LLM.Azure/README.md) now implements complete native Chat/Responses/text Completions JSON and SSE with explicit v1/dated routing and API Key or borrowed external token authentication. Stable principal Sessions survive token rotation; configuration/principal changes fail. Native SSE preserves delayed filter/usage tails and holds terminal events through EOF/disposal; explicit native compaction preserves complete windows under a target feature declaration. Independent native Realtime uses explicit v1/preview deployment handshakes and preserves raw events under per-I/O authentication/byte guards. Responses WebSocket is unsupported; no live Azure authentication or model validation is claimed.

The independent [DeepSeek native tools client](src/Asgard.Loki.LLM.DeepSeek/README.md) exposes explicit Search/Fetch JSON calls with an application-supplied Key and fixed service root. Native query/page content and ranked results stay complete; these model-free auxiliary APIs require no model route or Session. Applications choose each call and own any retrieval/model workflow.

The independent [Vertex LLM provider](src/Asgard.Loki.LLM.GoogleVertex/README.md) implements native Gemini/Claude JSON/SSE with explicit full project/location/model resources and borrowed external authentication. It preserves native fields and fixed-target Sessions through token rotation. Express Gemini API Key JSON/SSE is now available through a separate provider in the same package; MaaS Chat JSON/SSE is also delivered; wire fixtures do not establish live acceptance of Developer API-only fields.

## Packages and dependencies

| Package | Responsibility |
| --- | --- |
| `Asgard.Loki.Infrastructure` | Shared capability-independent SSE/NDJSON framing and bounded HTTP byte transport |
| `Asgard.Loki.Abstractions` | Generic client and module builder contracts |
| `Asgard.Loki` | Client implementation and module lifecycle |
| `Asgard.Loki.LLM` | Native LLM protocols, routing, Session storage and registration |
| `Asgard.Loki.Audio` | Independent speech, audio-file transcription/translation contracts, routing and results |
| `Asgard.Loki.Image` | Independent image contracts, routing and results |
| `Asgard.Loki.Embedding` | Independent native embedding contracts, routing and results; custom adapters |
| `Asgard.Loki.Rerank` | Independent native reranking contracts, routing and results; custom adapters |
| `Asgard.Loki.Rerank.Cohere` | Native Cohere v1/v2 reranking and provider-owned ranking views |
| `Asgard.Loki.Rerank.Jina` | Native Jina text/multimodal/listwise reranking and provider-owned views |
| `Asgard.Loki.Rerank.Ali` | Native DashScope nested reranking and provider-owned ranking views |
| `Asgard.Loki.Rerank.SiliconFlow` | Native SiliconFlow scalar/text/image reranking and provider-owned views |
| `Asgard.Loki.Rerank.SGLang` | Native SGLang score arrays and provider-owned ranking views |
| `Asgard.Loki.Rerank.OpenAICompatible` | Explicit compatible rerank results with original indices, scores and candidate echoes |
| `Asgard.Loki.Embedding.Azure` | Native deployment-scoped Azure embeddings and original batch vectors |
| `Asgard.Loki.Embedding.OpenAI` | Native OpenAI embeddings provider; provider-owned vector views |
| `Asgard.Loki.Embedding.Google` | Native Google single/synchronous-batch embeddings and provider-owned views |
| `Asgard.Loki.Embedding.Ollama` | Native Ollama embedding inputs/settings, ordered vectors and provider-owned views |
| `Asgard.Loki.Embedding.Jina` | Native Jina text/media/fused inputs, dense/per-token embeddings and provider-owned views |
| `Asgard.Loki.Video` | Native video tasks and complete-target reference binding |
| `Asgard.Loki.Video.Volcengine` | Ark and signed native Jimeng Visual video generation providers |
| `Asgard.Loki.Video.Ali` | Native DashScope video task provider with explicit service selection |
| `Asgard.Loki.Video.Google` | Native Gemini Veo operations and explicit content transfer |
| `Asgard.Loki.Video.GoogleVertex` | Native Vertex Veo operations with complete resource/principal binding |
| `Asgard.Loki.Video.MiniMax` | Native MiniMax video v1/v2 provider with explicit protocol selection |
| `Asgard.Loki.Video.Kling` | Native Kling video provider with explicit operation and authentication |
| `Asgard.Loki.Video.Vidu` | Native Vidu text/image/start-end/reference generation and creations query |
| `Asgard.Loki.Video.XAI` | Native xAI generation/edit/extension, original status/results and HTTP metadata |
| `Asgard.Loki.Video.Midjourney` | Explicit unofficial proxy video calls and original task results |
| `Asgard.Loki.LLM.Volcengine` | Ark LLM provider |
| `Asgard.Loki.LLM.OpenAI` | OpenAI LLM provider and explicit external-auth Codex Responses/compact |
| `Asgard.Loki.LLM.Anthropic` | Native Claude Messages provider |
| `Asgard.Loki.LLM.Google` | Native Gemini Developer API provider |
| `Asgard.Loki.LLM.Ollama` | Native Ollama Chat/generate JSON/NDJSON; explicit anonymous or Bearer authentication |
| `Asgard.Loki.LLM.Azure` | Native Azure endpoints and provider-owned external token source |
| `Asgard.Loki.LLM.DeepSeek` | Native model-free Search / Fetch auxiliary calls with fixed service credentials |
| `Asgard.Loki.LLM.Tencent` | Native Hunyuan JSON/SSE with supplier-local TC3 authentication |
| `Asgard.Loki.LLM.Xunfei` | Native Spark WebSocket authentication and complete message windows |
| `Asgard.Loki.LLM.Cohere` | Native Cohere v1 Chat JSON/NDJSON; independent from Rerank |
| `Asgard.Loki.LLM.Cloudflare` | Native Workers AI text ai/run JSON/SSE; independent from compatible OpenAI profile |
| `Asgard.Loki.LLM.Coze` | Explicit Coze v3 JSON/SSE and status/messages; no polling or fake model route |
| `Asgard.Loki.LLM.Dify` | Explicit fixed-app chat JSON/SSE; no fake model route, polling, workflow hosting or file management |
| `Asgard.Loki.LLM.GoogleVertex` | Native Vertex Gemini, Claude, Express and MaaS target identity/authentication |
| `Asgard.Loki.LLM.AWS` | Native Bedrock Claude and Converse JSON/EventStream with borrowed External SigV4 or explicit Bedrock API Key credentials |
| `Asgard.Loki.LLM.Conversion` | Optional explicit conversion; references only LLM |
| `Asgard.Loki.Audio.Volcengine` | Volcengine speech provider |
| `Asgard.Loki.Audio.OpenAI` | Native OpenAI speech and multipart transcription/translation; independent Audio state |
| `Asgard.Loki.Audio.MiniMax` | Native MiniMax synchronous speech; hex audio and caller-consumed URL results |
| `Asgard.Loki.Audio.Cloudflare` | Native Workers AI audio with explicit raw-binary or JSON/Base64 input and original results |
| `Asgard.Loki.Audio.Azure` | Native Azure deployment-scoped speech, transcription and translation; independent API Key target |
| `Asgard.Loki.Image.OpenAI` | OpenAI image provider |
| `Asgard.Loki.Audio.SunoAPI` | Explicit native Suno-API music and lyrics task calls for Asgard.Loki. |
| `Asgard.Loki.Image.Ali` | Native Ali image JSON, interleaved SSE and caller-driven image tasks for Asgard.Loki. |
| `Asgard.Loki.Image.Azure` | Native deployment-scoped Azure image generation and multipart editing for Asgard.Loki. |
| `Asgard.Loki.Image.Google` | Native Gemini image generation, editing inputs and original safety-aware SSE for Asgard.Loki. |
| `Asgard.Loki.Image.GoogleVertex` | Native resource-scoped Vertex Gemini images with explicit Bearer or API-Key authentication for Asgard.Loki. |
| `Asgard.Loki.Image.MiniMax` | Native MiniMax image generation and subject references with original URL/Base64 results for Asgard.Loki. |
| `Asgard.Loki.Image.Midjourney` | Explicit native proxy image operations and original bound task outcomes |
| `Asgard.Loki.Image.Replicate` | Native Replicate model image predictions and explicit model-input upload for Asgard.Loki. |
| `Asgard.Loki.Image.SiliconFlow` | Native SiliconFlow image generation with original images, timings and seed for Asgard.Loki. |
| `Asgard.Loki.Image.Volcengine` | Native Ark JSON/SSE and independently signed Jimeng Visual image generation for Asgard.Loki. |
| `Asgard.Loki.Image.XAI` | Native xAI JSON image editing with original ordered image references and complete results for Asgard.Loki. |
| `Asgard.Loki.Image.Zhipu` | Native Zhipu v4 image generation with original image carriers, content filtering and metadata for Asgard.Loki. |

The entry package depends only on abstractions. Capability packages do not reference each other. Providers reference their own capability, optional shared Infrastructure and necessary standard components. Installing OpenAI images does not install LLM or audio providers.

These are the package identities produced by this checkout. The PR/release CI configuration builds and verifies the full package set when invoked and attaches them to that workflow run. The separate manual `Publish NuGet` workflow supports OIDC login verification and explicit publication; its default `verify` mode does not upload packages. See [release instructions](docs/releasing.md). Local preview packages can be created with `dotnet pack`.

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

`loki.LLM.ChatCompletions.CompleteAsync(...)` and `.StreamAsync(...)` use `ChatCompletionRequest`, whose `Messages` is an unmodified JSON array. Chat and Responses expose an extension parameter dictionary for fields such as tools, reasoning and caching. `stream` is controlled by the selected method.

A route may contain any valid combination of server, actual model, credential and provider. New Sessions use the configured selector; existing Sessions always reuse the stored complete binding. Switching protocol or public model within a Session is rejected.

Custom LLM providers can explicitly support `LlmCredential.Anonymous("local-instance")` through `ILlmCredentialSupport`. It retains a stable credential identity in the complete Session binding; a missing Key never selects this mode automatically. Existing providers without that optional contract remain API-Key-only. The separate [Ollama LLM provider](src/Asgard.Loki.LLM.Ollama/README.md) now implements native Chat/generate JSON/NDJSON and explicit header isolation; cloud token refresh/signing remains pending; see the [LLM credential contract](src/Asgard.Loki.LLM/README.md#explicit-credential-kinds).

### Native Spark contract

`loki.LLM.Spark.CompleteAsync` / `.StreamAsync` require an explicit `LlmProtocol.Spark` target and `ISparkAdapter`. `SparkRequest.Model` selects the public route; only native Parameters reach the adapter, with the selected actual model mapped to `parameter.chat.domain`. The SDK adds no root model/stream, AppID or message history. `SparkEvent.Data` retains complete native messages; `SparkCompletion.Events` retains their entire ordered window, including plugins, all text items, native statuses and usage. It does not synthesize a single text completion or sum incremental usage. The [Xunfei provider](src/Asgard.Loki.LLM.Xunfei/README.md) implements native signed WebSocket transport and explicit normal-close validation. See the [native contract and ownership rules](src/Asgard.Loki.LLM/README.md#native-spark-contract).

## Native Hunyuan contract

`loki.LLM.Hunyuan.CompleteAsync` / `.StreamAsync` require an explicit `LlmProtocol.Hunyuan` target and `IHunyuanAdapter`. `HunyuanRequest.Parameters` retains native PascalCase messages, tool schema/argument strings, tool associations, reasoning, multimodal and unknown fields. The methods own native `Model`/`Stream` and use the existing complete Session binding. Complete results retain the entire `Response` envelope; stream records retain their original flat JSON and SSE metadata. Native IDs, finish reasons, optional indices and trailing metadata are not converted into another protocol. The [Tencent supplier](src/Asgard.Loki.LLM.Tencent/README.md) implements native TC3 JSON/SSE and documents explicit TokenHub-compatible reuse. See the [native contract and adapter responsibilities](src/Asgard.Loki.LLM/README.md#native-hunyuan-contract).

## Native text Completions contract

Providers implementing `ITextCompletionsAdapter` can register explicit `LlmProtocol.TextCompletions` targets and expose `loki.LLM.TextCompletions.CompleteAsync` / `.StreamAsync`. `TextCompletionRequest.Parameters` retains native prompt text/token batches, suffix, echo, logprobs and unknown fields; it is not converted into Chat messages. Complete JSON and `TextCompletionEvent` retain original choices, indices, native fields and SSE metadata. Method-owned `stream` and actual-model mapping use the same complete Session binding, with no history management, automatic conversion or retry. The [Azure provider](src/Asgard.Loki.LLM.Azure/README.md#native-text-completions) supplies native JSON/SSE transport. See the [native contract](src/Asgard.Loki.LLM/README.md#native-text-completions).

### Native Responses compaction

For a native OpenAI or Azure target, explicitly configure `Protocols = LlmProtocol.Responses` and `Features = LlmTargetFeatures.ResponsesCompaction`. Both the target flag and an `IResponsesCompactionAdapter` are required. Ordinary Responses support does not imply compact support; Ark and converting providers do not currently advertise this operation.

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

Compaction is one explicit non-streaming request to `/responses/compact`; the SDK does not trigger it automatically or prune/decrypt the returned window. The optional `PreviousResponseId` uses the same Session ownership checks as creation. A compaction result ID is not registered as a generated response ID for continuation. Calls remain bound to the complete original target, and unsupported features never cause rerouting or retries. Enabling/changing a target feature changes its binding fingerprint; default `Features.None` preserves existing basic-target serialization. See the [OpenAI provider guide](src/Asgard.Loki.LLM.OpenAI/README.md) and [Azure provider guide](src/Asgard.Loki.LLM.Azure/README.md#explicit-native-compaction).

### Native Responses WebSocket

Enable `LlmTargetFeatures.ResponsesWebSocket` on a native OpenAI Responses target, then call `loki.LLM.Responses.OpenWebSocketAsync(publicModel, callOptions, cancellationToken)`. The caller owns the connection, sends native JSON and reads all relevant lane/terminal events. Native `stream_id`, tools, opaque state, unknown fields and errors are retained. Session and response ownership stay bound to the original complete target. No reconnect, retry, tool execution or history replay is performed. Initialization, custom factory/resource limits and a complete usage example are in the [OpenAI provider guide](src/Asgard.Loki.LLM.OpenAI/README.md#native-responses-websocket). This support is verified offline with in-memory sockets; no live OpenAI verification has been performed.

## Session storage

`DistributedCacheLlmSessionStore` depends on Microsoft's `IDistributedCache`; Redis is one implementation. It does not create or own the cache connection.

First binding requires explicit `ILlmSessionLock` / `ILlmSessionLease` coordination. For multiple processes, that implementation must coordinate through the same distributed system and signal lease loss. Plain cache Get/Set is not atomic. An existing distributed-lock service can be adapted to this small contract without referencing the rest of its framework.

Bindings store a target/configuration fingerprint, never the API Key. Original response IDs are associated with their Session. Missing, expired, foreign or changed bindings fail explicitly; there is no silent provider/model/key fallback. Active bindings use sliding expiration, defaulting to 24 hours.

Redis preserves routing identity, not TCP connections or provider prompt caches. Unknown execution outcomes are not automatically retried. The caller owns Session authorization and isolation, and should choose an application-specific cache prefix.

## Volcengine speech

The following example uses V3 HTTP. For explicit V1 HTTP query or binary WebSocket submit, original native JSON and completion rules, see the [Volcengine speech provider](src/Asgard.Loki.Audio.Volcengine/README.md). Volcengine documents V1 as not recommended; no real-provider speech verification is claimed.

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

Tests use xUnit v3 / Microsoft.Testing.Platform, `MemoryDistributedCache`, an in-process test coordinator and substitute transports. They require no provider keys, network or Redis. Every ordinary test project must discover and execute tests, including Video.

Local protocol tests are not live-provider verification. Real account permissions, model availability and Redis cross-process coordination require separate integration checks. No real provider calls or package publication are performed by the unit tests.

The [test configuration contract](docs/testing.md) and [.env.example](.env.example) define exactly four variables for the separate Volcengine LLM live suite: opt-in, API Key, Chat model and Responses model. Run it explicitly with `pwsh -File eng/Test-Live.ps1`. The script supports Windows current-user variables and does not load `.env` files. Ordinary tests and automatic CI stay offline even when live credentials exist.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development and validation rules, [SECURITY.md](SECURITY.md) for reporting vulnerabilities, and [docs/releasing.md](docs/releasing.md) for the package release checklist. Development uses local validation. GitHub CI runs on Windows/Linux only for PRs into `main` or reuse by the explicit NuGet release workflow; ordinary pushes and standalone manual CI runs are disabled. Dependabot checks NuGet dependencies and GitHub Actions weekly.

## License

Licensed under the [Apache License, Version 2.0](LICENSE).

### Native Cohere v1 Chat contract

`loki.LLM.CohereChat.CompleteAsync` / `.StreamAsync` require an explicit `LlmProtocol.CohereChat` target and `ICohereChatAdapter`. Only actual target model and method stream mode change; full message/history, native tools/results, references and unknown JSON stay intact. Results keep response_id and generation_id distinct, while NDJSON events use event_type without synthetic SSE metadata. The [Cohere supplier](src/Asgard.Loki.LLM.Cohere/README.md) owns native HTTP/NDJSON and terminal integrity; offline support makes no live-service or automatic conversion claim. See the [native contract](src/Asgard.Loki.LLM/README.md#native-cohere-v1-chat-contract).

### Explicit Cloudflare compatible calls

[OpenAI supplier configuration](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-cloudflare-workers-ai-profile) supports explicit account-scoped Workers AI Chat JSON/SSE and currently supported non-streaming GPT-OSS Responses Create. Account root, actual model and API token remain one Session target; tool associations, reasoning/encrypted data, settings and unknown fields stay native. This profile does not infer model availability or convert the separate native ai/run text wire. Offline protocol tests do not establish live account/model acceptance.

### Native Workers AI text contract

`.LLM.WorkersAiText` requires an explicit `LlmProtocol.WorkersAiText` target and `IWorkersAiTextAdapter`. The public model stays route metadata; the selected actual model belongs in the native URL, while prompt/messages and all native parameters stay in their original body structure. Complete results retain the native envelope. Streaming events preserve actual SSE JSON or `[DONE]` data with separate transport identity; no JSON terminal is fabricated. The [Cloudflare supplier](src/Asgard.Loki.LLM.Cloudflare/README.md) implements native HTTP/SSE; see the [contract and adapter responsibilities](src/Asgard.Loki.LLM/README.md#native-workers-ai-text-contract).

### Explicit Coze v3 bot chat

[CozeChatClient](src/Asgard.Loki.LLM.Coze/README.md) sends one native create/SSE/status/chat-messages request per method and retains complete acknowledgements, message arrays and original two-level stream events with actual done. Bot ID is service configuration, not an underlying model selection. Native references keep server/Bot/credential/implementation/ID affinity without a fabricated model Session, automatic polling, tool execution or bot/workflow administration. Offline protocol support does not establish live Bot access or answer quality.

### Explicit Codex backend calls

The existing [OpenAI package](src/Asgard.Loki.LLM.OpenAI/README.md#explicit-codex-backend-responses) also provides a separate CodexLlmProvider for explicit backend Responses JSON/SSE and compact. Complete root/model/principal/account/source configuration stays bound to the existing SDK Session, while short-lived tokens can rotate. Original tools/reasoning/encrypted/schema/unknown fields are preserved; no login, token refresh, account management, parameter sanitization or automatic retry is added. These backend-specific calls are offline-tested, not a stable public API or live entitlement claim.
