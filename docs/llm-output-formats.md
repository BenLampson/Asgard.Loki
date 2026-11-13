# LLM output formats

Loki preserves native output-format fields through request `Parameters`. A field being forwarded does not establish that a particular endpoint, model, deployment or account supports it. JSON mode requests valid JSON; JSON Schema output additionally requests a particular structure. Tool input schemas and strict tool calls are separate features.

## Target declarations and validation

`LlmTarget.AllowedOutputFormats` maps each **SDK call protocol** to an explicit list of `text`, `json_object` and/or `json_schema`. It belongs to the complete server/model/credential/adapter target, not to a supplier name or a public model alias. Declare each configured endpoint independently.

| Declaration | Behavior |
| --- | --- |
| `null`, an empty dictionary, or no entry for the invoked protocol | Undeclared; preserves the existing native forwarding behavior, without certifying support |
| A protocol entry with an empty list | Rejects every explicit format recognized for that protocol; omission remains allowed |
| A protocol entry with a nonempty list | Only the listed explicit format intents are allowed |

Registration validates every target, including unselected alternatives, and freezes both the dictionary and its lists. Unknown/combined/disabled protocol keys, null lists, duplicate entries and unknown format names fail registration. Declaration order has no effect on the Session fingerprint. Changing the actual declaration invalidates existing Sessions; an omitted new property preserves legacy target fingerprint bytes.

Complete and streaming SDK calls inspect the original request snapshot against the selected target **before invoking its adapter**. A first-call policy failure occurs before creating a Session binding. The target selector is not filtered by format: an unsupported selected or already-bound target fails without selecting another one. If first-binding coordination returns another concurrent call's binding, that actual target is checked again. `NotSupportedException` identifies the target, protocol and native field path. Malformed explicit format envelopes throw `ArgumentException` when a declaration is active. No format, Schema, prompt, tool definition or protocol is removed, rewritten or downgraded.

An omitted format does not imply an explicit `text` setting. Explicit null/invalid format objects cannot bypass a declaration; optional parent containers without a format remain omitted. These checks constrain format intent, not the entire supplier schema, JSON Schema dialect, model entitlement, prompt requirements, incompatible tool/RAG combinations or returned output. Applications must still inspect native completion status, truncation/refusal/errors and validate returned JSON as appropriate. An upstream rejection remains an ordinary native `LlmUpstreamException`.

Responses WebSocket `response.create` checks the same `Responses` declaration on the frozen target before sending. Opening a socket has no output format to check. Compaction, Realtime, text/FIM, Moderations, native Nova, Hunyuan and Spark do not currently accept declaration entries; registration rejects these keys instead of silently ignoring them. This text-output policy does not govern Audio/Image media formats. Direct adapter calls bypass SDK orchestration; callers of those lower-level interfaces own their validation.

## Native format envelopes supported by the checker

| SDK protocol | Native request field | Classification |
| --- | --- | --- |
| `ChatCompletions` | `response_format.type` | Native `text`, `json_object`, `json_schema`; Chat Schema remains inside `response_format.json_schema` |
| `Responses` | `text.format.type` | Native `text`, `json_object`, `json_schema`; Schema stays in `text.format.schema` |
| `Responses` with Perplexity's native envelope | Root `response_format.type` | Native root format remains at the root; both known Responses format paths are checked independently when supplied, without selecting precedence or moving fields |
| `ClaudeMessages` | `output_config.format.type` | Native JSON Schema envelope; `output_config.effort` alone is not a format |
| `Gemini` | `generationConfig.responseMimeType` plus optional `responseSchema` or `responseJsonSchema` | `text/plain` → `text`; `application/json` → `json_object`, or `json_schema` when a Schema field is present |
| `Gemini` | `generationConfig.responseFormat.text` | `mimeType: TEXT_PLAIN` → `text`; `APPLICATION_JSON` → `json_object`, or `json_schema` with `schema` |
| `OllamaChat`, `OllamaGenerate` | `format` | Empty string → `text`; `"json"` → `json_object`; a native Schema object → `json_schema` |
| `BedrockConverse` | `outputConfig.textFormat.type` | Native `json_schema`; `structure.jsonSchema.schema` remains a JSON **string** |
| `CohereChat` | `response_format` | `type: json_object` → `json_object`; the same type with a `schema` field → `json_schema`, without rewriting the type |
| `WorkersAiText` | `response_format.type` | Native JSON-object/JSON-Schema envelope, subject to the selected model |

Gemini's two legacy Schema fields cannot be combined; modern text and legacy text envelopes cannot be mixed. Other MIME values are not treated as ordinary text/JSON. Modern configuration containing only other modalities or an empty text configuration does not invent a text-format intent. Typed Gemini Schema, JSON Schema, Cohere's schema object and Bedrock's schema string retain their native representations; this checker performs no schema conversion.

## Endpoint support and limitations

The following documented distinctions must be reflected in application-owned target declarations. These are native contract descriptions, not live acceptance results or SDK-maintained model profiles.

| Endpoint / protocol | Native documented format support | Configuration consequence |
| --- | --- | --- |
| [DeepSeek Chat](https://api-docs.deepseek.com/api/create-chat-completion/) | `response_format.type`: `text`, `json_object`; no Chat `json_schema` | Declare `["text", "json_object"]`; [JSON mode](https://api-docs.deepseek.com/guides/json_mode/) also requires a JSON instruction containing `json`, a suitable token limit, and may return empty content |
| [DeepSeek Responses](https://api-docs.deepseek.com/api/create-response/) | `text.format`: `text`, `json_object`, `json_schema` | Declare all three only for the separately selected Responses endpoint/model; send full stateless input |
| [DeepSeek Messages](https://api-docs.deepseek.com/guides/anthropic_api/) | `output_config` supports `effort` only | Declare `ClaudeMessages = []` to reject explicit output formats; thinking/effort or strict tools do not grant text Schema support |
| [Zhipu v4 Chat](https://docs.bigmodel.cn/api-reference/模型-api/对话补全) | Documents `text` and `json_object` | Declare `["text", "json_object"]`; [Responses](https://docs.bigmodel.cn/api-reference/response/创建-response) has its own `text.format` contract, not an inherited Chat declaration |
| [Moonshot/Kimi Chat](https://platform.kimi.ai/docs/api/chat), [Mistral Chat](https://docs.mistral.ai/api/endpoint/chat) | Document text, JSON mode and JSON Schema through `response_format` | Declare the subset accepted by the selected model/service; Kimi Partial Mode and JSON mode are separate requests |
| [Anthropic Messages](https://platform.claude.com/docs/en/build-with-claude/structured-outputs) | Native Schema output uses `output_config.format` on supported models | Declare `json_schema` only for a verified selected model; this does not imply a `json_object` mode |
| [MiniMax Messages](https://platform.minimax.io/docs/api-reference/text-anthropic-api) and other Messages-compatible services | Effort/thinking compatibility does not itself establish format support | Confirm `output_config.format` independently; use an empty list when no explicit format is supported/approved for the target |
| [Ollama](https://docs.ollama.com/capabilities/structured-outputs) | Local native `format` supports JSON mode/Schema; Ollama Cloud currently does not support structured outputs | Keep local and cloud targets separate; a local declaration is not a cloud capability grant |
| [Gemini](https://ai.google.dev/api/generate-content#v1beta.GenerationConfig) | Native MIME/Schema fields; supported constraints depend on the selected model and schema dialect | Declare the intended format categories without converting Schema dialects or inserting defaults |
| [Bedrock Converse](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_OutputConfig.html) | `outputConfig.textFormat`; [Schema is a string](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_JsonSchemaDefinition.html) | Declare `json_schema` only for the supported model; Claude InvokeModel uses a different envelope |
| [Cohere v1 Chat](https://docs.cohere.com/v1/reference/chat) | `response_format.type: json_object`, optionally `schema`; model and RAG/tool restrictions apply | Schema capability must be declared separately even though the native `type` remains `json_object` |
| [Cloudflare native Workers AI](https://developers.cloudflare.com/workers-ai/features/json-mode/) | JSON mode and Schema support depend on the model | Declare `WorkersAiText` independently of the compatible Chat or Responses targets |
| [Perplexity Agent Responses](https://docs.perplexity.ai/api-reference/agent-post) / Sonar Chat | Agent Responses uses root `response_format`; Sonar has its own schema/regex formats | Responses checks the native root field as well as standard `text.format`; no envelope conversion occurs. The declaration vocabulary covers text/JSON/Schema only, so other explicit formats such as regex fail under an active list |

OpenAI, Azure, Ark, DashScope, xAI, SiliconFlow, Baidu, Mistral, Moonshot, OpenRouter, Xinference, Submodel, vLLM and SGLang use their configured native or compatible envelopes as documented in their supplier READMEs. Shared protocol syntax is not a common guarantee for every model or deployment. In particular, router/downstream selection, hosted versus local deployments, model versions and thinking/tool combinations can narrow support. Loki neither queries those services nor infers format support from host/model names. Unsupported or unverified format claims should not be enabled merely because an adapter can forward the field.

## DeepSeek initialization and calls

The caller supplies `httpClientFactory`, `sessionStore`, an independent `deepSeekApiKey`, and actual selected Chat/Responses model names. The store's first-binding coordinator and named HTTP pipeline remain caller-owned; disable generation retries/hedging. Model values below are configuration variables, not an SDK model catalog.

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using System.Text.Json;
using System.Text.Json.Nodes;

var provider = new OpenAiLlmProvider(httpClientFactory, id: "deepseek-native");
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    Providers = [provider],
    SessionStore = sessionStore,
    Credentials = [new LlmCredential("deepseek-key", deepSeekApiKey)],
    Routes =
    [
        new LlmRoute
        {
            Model = "deepseek-chat",
            Targets = [new LlmTarget
            {
                Id = "ds-chat", ProviderId = provider.Id,
                BaseAddress = new("https://api.deepseek.com/v1/"),
                UpstreamModel = selectedChatModel, CredentialId = "deepseek-key",
                Protocols = LlmProtocol.ChatCompletions,
                AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>>
                {
                    [LlmProtocol.ChatCompletions] = ["text", "json_object"]
                }
            }]
        },
        new LlmRoute
        {
            Model = "deepseek-responses",
            Targets = [new LlmTarget
            {
                Id = "ds-responses", ProviderId = provider.Id,
                BaseAddress = new("https://api.deepseek.com/"),
                UpstreamModel = selectedResponsesModel, CredentialId = "deepseek-key",
                Protocols = LlmProtocol.Responses,
                AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>>
                {
                    [LlmProtocol.Responses] = ["text", "json_object", "json_schema"]
                }
            }]
        }
    ]
}).Build();

var chat = await loki.LLM.ChatCompletions.CompleteAsync(new ChatCompletionRequest
{
    Model = "deepseek-chat",
    Messages = [new JsonObject
    {
        ["role"] = "user",
        ["content"] = "Return JSON such as {\"answer\":\"Paris\"}. What is France's capital?"
    }],
    Parameters = new()
    {
        ["response_format"] = JsonSerializer.SerializeToElement(new { type = "json_object" }),
        ["max_tokens"] = JsonSerializer.SerializeToElement(4096)
    }
}, new LlmCallOptions { SessionId = "ds-chat-json" }, cancellationToken);

var response = await loki.LLM.Responses.CreateAsync(new ResponseRequest
{
    Model = "deepseek-responses",
    Input = JsonValue.Create("What is France's capital?"),
    Parameters = new()
    {
        ["text"] = JsonSerializer.Deserialize<JsonElement>("""
        {"format":{"type":"json_schema","name":"capital","schema":{
          "type":"object","properties":{"answer":{"type":"string"}},
          "required":["answer"],"additionalProperties":false
        }}}
        """),
        ["max_output_tokens"] = JsonSerializer.SerializeToElement(4096)
    }
}, new LlmCallOptions { SessionId = "ds-responses-schema" }, cancellationToken);
```

Use `ChatCompletions.StreamAsync` or `Responses.StreamAsync` with the same request shape and enumerate the full stream. Chat `json_schema` fails locally under this Chat declaration, even if the route also contains a Schema-capable target. Choosing Responses instead is an explicit application decision requiring its own target and Session, not a fallback performed by Loki. DeepSeek Responses is stateless: later requests must contain full input/history; a local Session does not supply remote conversation storage.

For DeepSeek Messages, configure the separate Anthropic provider/target as described in its [README](../src/Asgard.Loki.LLM.Anthropic/README.md#explicit-deepseek-messages), with:

```csharp
AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>>
{
    [LlmProtocol.ClaudeMessages] = []
}
```

Ordinary Messages and `output_config.effort` remain native; an explicit `output_config.format` is rejected before model invocation.

## Explicit conversion

`LlmTarget.AllowedOutputFormats` checks the protocol used by the SDK caller, including when a converting decorator is registered. It does not infer which protocol that decorator will emit. To restrict the **final native request produced by cross-protocol conversion**, additionally configure `LlmConversionOptions.TargetSettings.AllowedOutputFormats` on the fixed converting provider. For example, a conversion whose actual upstream is DeepSeek Chat should declare `["text", "json_object"]` there. A disallowed emitted Schema produces field-level `LlmConversionException` diagnostics even with `AllowLoss = true`; it is not downgraded into JSON mode. `TargetSettings` does not apply to same-protocol copies; use the target map for SDK calls through that native branch. See [conversion settings](../src/Asgard.Loki.LLM.Conversion/README.md#caller-declared-target-settings).

## Verification

Offline tests cover complete/streaming SDK calls, original native envelopes, malformed/unknown/disallowed formats, omitted formats and tool schemas, immutable request/configuration snapshots, target selection and concurrent binding, cross-instance policy fingerprints and WebSocket sends. Fake adapters/HTTP and `MemoryDistributedCache` do not establish real upstream format enforcement. DeepSeek and other supplier live acceptance remains as documented in each package; the separate Ark [parameter verification](llm-parameter-verification.md) applies only to its recorded model/account/scenarios.
