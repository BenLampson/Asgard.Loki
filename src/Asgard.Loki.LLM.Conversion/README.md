# Asgard.Loki.LLM.Conversion

Optional, explicit protocol conversion for the Asgard.Loki .NET SDK. This package references **only `Asgard.Loki.LLM`**. Native provider packages never reference it or one another. It contains no HTTP server, model execution loop, tool executor or gateway services.

## Conversion matrix

| Source \ destination | Chat Completions | Responses | Claude Messages | Gemini |
| --- | --- | --- | --- | --- |
| Chat Completions | Copy | Supported subset | Supported subset | Supported subset |
| Responses | Supported subset | Copy | Supported subset | Supported subset |
| Claude Messages | Supported subset | Supported subset | Copy | Supported subset |
| Gemini | Supported subset | Supported subset | Supported subset | Copy |

Every off-diagonal direction has independent request, complete-response, streaming and public-SDK/HTTP tests. This matrix describes the common subset below, **not arbitrary lossless conversion of all native features**.

| Concern | Conversion contract |
| --- | --- |
| Content | Ordered user/assistant text, system instructions, inline Base64 images |
| Functions | Definitions with JSON Schema, named/auto/required/none choice, original call IDs, arguments and associated results |
| Gemini results | Must already be JSON object text; the SDK does not wrap arbitrary text in an invented result schema |
| Parameters | Temperature, top-p, explicit output limits; top-k and stop where the destination supports them |
| Structured output | JSON Schema wrappers across all four protocols; schema copied unchanged. Gemini `responseJsonSchema` and modern `responseFormat.text` supported; its separate typed `responseSchema` dialect is still diagnosed. Plain JSON/text modes map between OpenAI and Gemini |
| Reasoning settings | Qualitative effort, explicit Claude/Gemini budget or dynamic/off mode, and summary/display intent where representable; no model-name heuristics or calculated budgets |
| Parallel functions | Chat/Responses `parallel_tool_calls` ↔ Claude `disable_parallel_tool_use`; Gemini has no equivalent switch and produces a diagnostic |
| Responses | Creation in the common stateless subset; no cross-protocol `previous_response_id`, `store` or background semantics |
| Responses output | Single candidate, ordinary text/function calls and supported finish reasons |
| Usage | Base totals, cache reads and reasoning counts with native accounting conventions; OpenAI details transfer between Chat/Responses. Claude cache-write breakdown or unsupported nonzero details diagnose loss |
| Streaming | Immediate text deltas; function arguments buffered until complete JSON; correct target start/item/delta/done/terminal sequence |
| Diagnostics | Unknown fields, hosted tools, unsupported media/settings and unrepresentable usage details are rejected by default |
| Opaque reasoning/signatures | Cross-protocol conversion always rejects these, even with `AllowLoss`; use native APIs |

Encrypted Responses `compaction` items are also a hard native-only boundary in request, response and stream conversion, including when ordinary loss is allowed. Same-protocol copies preserve them unchanged. `ConvertingLlmProvider` does not implement or advertise the optional native compaction operation; configure a separate native target for it.

Streams are single-call state machines, not resumable event subscriptions. Missing terminal events, invalid/incomplete tool arguments, inconsistent final Responses output, unsupported interleaving or upstream errors fail explicitly. A late failure may occur after earlier deltas have been returned; callers must observe the full enumeration. No successful terminal is emitted for a truncated source stream. Direct same-protocol stream copies leave native transport validation to the provider.

Tool results consume one pending call exactly once. Gemini results without IDs must match a unique pending function name; completed history is excluded, and generated IDs avoid explicit IDs throughout the request. Stream validation checks closed blocks, item identities, sequence numbers, done snapshots, duplicate terminals and mutations after completion. Text cannot overtake an earlier buffered tool. Explicit length/filter finishes retain incomplete Responses item status. Unknown nested fields are diagnosed even inside recognized events. Missing usage remains unknown; conversion to Claude requires final input/output counts rather than inventing them. Claude stream start usage excludes cached input from `input_tokens`.

## Explicit generation settings

`reasoning_effort`, Responses `reasoning.effort`, Claude `output_config.effort` and Gemini `thinkingConfig.thinkingLevel` map qualitative intent. Supported levels remain model-dependent; the SDK does not infer capabilities from model names or promise equal token use/quality between suppliers. Claude uses adaptive thinking for an effort-only request. `TargetThinkingBudgetTokens` explicitly selects manual Claude thinking instead; it must be at least 1024 and below the caller's output limit. Output limits are never raised automatically. Manual mode is only usable on models that support it.

Exact numeric budgets transfer between Claude and Gemini; zero/off and dynamic (-1/adaptive) remain distinct. An OpenAI target cannot represent a numeric thinking budget. `TargetReasoningEffort` can explicitly replace it, but produces `reasoning_budget_replaced` and still requires `AllowLoss = true`. Supplying both budget and effort can also be unrepresentable in a destination. Explicit `false` and omitted/null settings are not treated as interchangeable.

JSON Schema bodies are copied unchanged into OpenAI `response_format`/`text.format`, Claude `output_config.format`, or Gemini's selected text-format envelope. The default `GeminiOutputFormat.LegacyJsonSchema` retains `responseMimeType` + `responseJsonSchema`; explicitly choose `GeminiOutputFormat.ResponseFormat` for `generationConfig.responseFormat.text` with `APPLICATION_JSON` / `TEXT_PLAIN` and optional `schema`. Both source envelopes are recognized without model-name heuristics. Native Gemini calls and same-protocol copies remain unchanged, including future fields.

Modern and legacy text envelopes cannot be combined: the converter rejects ambiguous text settings even with `AllowLoss`. Unsupported audio/image format settings and unknown fields inside the modern envelope retain precise diagnostics; an omitted text format does not invent JSON/plain intent. A schema requires the JSON MIME and an object root in this common subset. The legacy typed `responseSchema` dialect is a separate pending mapping, not an alias for JSON Schema. Current Google references mark that old field deprecated; support/selection still depends on the configured upstream.

No JSON Schema constraints are deleted or translated into descriptions, including nested references, zero/false/null values and extension keywords. Supported keywords/combinations still depend on the actual upstream. For unnamed Claude/Gemini schemas, an OpenAI target requires caller-provided `DefaultSchemaName`. OpenAI wrapper name/description and non-strict enforcement cannot be represented exactly in Claude/Gemini: they produce field diagnostics and require explicit loss consent. Claude has no equivalent generic OpenAI JSON-object mode.

```csharp
var options = new LlmConversionOptions
{
    DefaultMaxTokens = 4096,
    DefaultSchemaName = "result",
    GeminiOutputFormat = GeminiOutputFormat.ResponseFormat // explicit target choice
};
// Add AllowLoss = true only for reviewed metadata/enforcement differences.
// Set TargetThinkingBudgetTokens only for a target supporting manual thinking.
```

Summary/display intent maps to Responses `reasoning.summary`, Claude thinking `display`, and Gemini `includeThoughts` where possible. Request-setting support does not authorize conversion of signed thinking or encrypted reasoning returned by the model; those responses still require native APIs. Unsupported summary detail levels and unknown nested settings remain diagnosed. All options and the conversion algorithm version participate in Session binding identity; changed options invalidate old bindings. This checkpoint uses `conversion-v4`; Sessions from the previous conversion algorithm are rejected rather than silently rebound. Native-provider Session identities are not changed by this conversion-package version.

Known descriptive response envelope fields and executed-request echoes (for example service tier, metadata and Responses sampling/tool configuration) stay in `OriginalData` / `Metadata.Conversion.UpstreamData`. They are not injected into the converted content or treated as a new request/continuation. Business output and future unclassified fields still undergo strict validation. This permits realistic native response envelopes without silently reusing upstream execution settings.

Field references checked for this implementation: [OpenAI structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs), [OpenAI reasoning](https://developers.openai.com/api/docs/guides/reasoning), [Claude structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs), [Claude thinking](https://platform.claude.com/docs/en/build-with-claude/thinking), and [Gemini generation configuration](https://ai.google.dev/api/generate-content#GenerationConfig).

## Register an explicit converting provider

```csharp
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Anthropic;
using Asgard.Loki.LLM.Conversion;

var native = new AnthropicLlmProvider(httpFactory);
var converting = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages,
    id: "chat-via-claude", options: new LlmConversionOptions { DefaultMaxTokens = 512 });

// Register only converting in this route's LlmOptions.Providers.
var target = new LlmTarget
{
    Id = "claude-chat-target", ProviderId = converting.Id,
    BaseAddress = new Uri("https://api.anthropic.com/v1/"),
    UpstreamModel = claudeModel, CredentialId = "claude-key",
    Protocols = LlmProtocol.ChatCompletions // caller-facing protocol
};
// Call loki.LLM.ChatCompletions with a complete, compatible request/history.
// The decorator calls Claude natively on the already-bound target.
```

Use a separate provider ID/target when exposing the original native protocol as well. The decorator borrows the supplied provider. No provider, cache, coordinator or factory is disposed by this package. The entire target, wrapped implementation, fixed upstream protocol and conversion options participate in the Session fingerprint. Configuration changes invalidate old bindings instead of silently changing behavior. Caller-managed `LlmTarget.Version` still tracks external HTTP/configuration semantics.

`result.Metadata.Conversion` identifies upstream/public protocols, retains complete upstream JSON (or the source event for an emitted event), and returns accumulated request/response diagnostics. Converted SSE IDs/retry hints are not represented as native upstream delivery identities. Unknown source events allowed by policy can produce no target event; their diagnostics remain accumulated and appear on later emitted/final events. Native calls retain their original JSON and SSE metadata with `Conversion == null`.

## Pure data API

```csharp
var result = LlmProtocolConverter.ConvertRequest(originalJson,
    LlmProtocol.ChatCompletions, LlmProtocol.Responses);
// result.Data, result.OriginalData and result.Diagnostics are independent snapshots.

var state = new LlmProtocolStreamConverter(LlmProtocol.ClaudeMessages,
    LlmProtocol.Responses);
foreach (var sourceEvent in sourceEvents)
    foreach (var converted in state.Push(sourceEvent)) Consume(converted.Data);
foreach (var converted in state.Complete()) Consume(converted.Data);
```

The default policy throws `LlmConversionException` with field-level `Diagnostics`. Explicit `new LlmConversionOptions { AllowLoss = true }` permits diagnosed omissions where safe to represent; it does not override hard constraints such as opaque signatures, continuation ownership, malformed arguments or ambiguous tool links. Requests are never mutated. Claude's required token limit must come from the source request or `DefaultMaxTokens`; the converter does not choose it implicitly.

Offline tests use xUnit v3, hand-maintained native fixtures and assertions on final HTTP payloads and streaming lifecycles. They do not establish model quality or provider acceptance for every account/model. No live Claude/Gemini/conversion requests have been performed. See [architecture](../../docs/architecture.md) and [testing](../../docs/testing.md).

When the source provides no creation timestamp, converted OpenAI `created`/`created_at` uses zero to mark unknown time, rather than claiming the current time was the provider creation time. A missing source response ID gets a conversion-local ID; source IDs are otherwise preserved. Stream IDs remain stable throughout the conversion.

Usage mappings follow the native [Gemini UsageMetadata](https://ai.google.dev/api/generate-content#UsageMetadata) and [Claude cache counters](https://platform.claude.com/docs/en/build-with-claude/prompt-caching). Gemini thought tokens are separate from candidate tokens; Claude cached input is separate from uncached `input_tokens`. These mappings expose usage only, without computing prices.
