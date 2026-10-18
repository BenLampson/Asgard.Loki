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
| Structured output / reasoning effort | Native field mapping between Chat and Responses; other destinations diagnose unsupported settings |
| Responses | Creation in the common stateless subset; no cross-protocol `previous_response_id`, `store` or background semantics |
| Responses output | Single candidate, ordinary text/function calls and supported finish reasons |
| Usage | Base totals, cache reads and reasoning counts with native accounting conventions; OpenAI details transfer between Chat/Responses. Claude cache-write breakdown or unsupported nonzero details diagnose loss |
| Streaming | Immediate text deltas; function arguments buffered until complete JSON; correct target start/item/delta/done/terminal sequence |
| Diagnostics | Unknown fields, hosted tools, unsupported media/settings and unrepresentable usage details are rejected by default |
| Opaque reasoning/signatures | Cross-protocol conversion always rejects these, even with `AllowLoss`; use native APIs |

Streams are single-call state machines, not resumable event subscriptions. Missing terminal events, invalid/incomplete tool arguments, inconsistent final Responses output, unsupported interleaving or upstream errors fail explicitly. A late failure may occur after earlier deltas have been returned; callers must observe the full enumeration. No successful terminal is emitted for a truncated source stream. Direct same-protocol stream copies leave native transport validation to the provider.

Tool results consume one pending call exactly once. Gemini results without IDs must match a unique pending function name; completed history is excluded, and generated IDs avoid explicit IDs throughout the request. Stream validation checks closed blocks, item identities, sequence numbers, done snapshots, duplicate terminals and mutations after completion. Text cannot overtake an earlier buffered tool. Explicit length/filter finishes retain incomplete Responses item status. Unknown nested fields are diagnosed even inside recognized events. Missing usage remains unknown; conversion to Claude requires final input/output counts rather than inventing them. Claude stream start usage excludes cached input from `input_tokens`.

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
