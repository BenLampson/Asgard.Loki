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
| Functions | JSON Schema or Gemini typed `parameters` input; explicit typed target parameters; named/auto/required/none choice, original call IDs, arguments and associated results |
| Custom tools | Opt-in Responses free-text custom → Chat/Claude/Gemini functions, complete/stream restoration and history replay |
| Namespace tools | Separate opt-in flattens Responses namespace functions/custom tools; restores namespace/name/kind through complete calls, streams and replay; group descriptions require diagnosed loss consent |
| Hosted search | First slice: explicit Responses / classic Claude / Gemini request configuration, six directions; known result/citation diagnostics and hard native-only encrypted search state. Full result/citation mapping remains pending |
| Gemini results | Ordinary functions require JSON object text. Opt-in custom conversion explicitly wraps its arbitrary text result as `{"output": text}` |
| Parameters | Temperature, top-p, explicit output limits; top-k and stop where the destination supports them |
| Structured output | JSON Schema wrappers across all four protocols; JSON Schema bodies copied unchanged by default. Gemini `responseJsonSchema`, modern `responseFormat.text` and typed `responseSchema` input supported. Explicit `LegacySchema` writes representable typed output schemas. Plain JSON/text modes map between OpenAI and Gemini |
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

Modern and legacy text envelopes cannot be combined: the converter rejects ambiguous text settings even with `AllowLoss`. Unsupported audio/image format settings and unknown fields inside the modern envelope retain precise diagnostics; an omitted text format does not invent JSON/plain intent. A schema requires the JSON MIME and an object root in this common subset. Current Google references mark the legacy typed `responseSchema` field deprecated; its source dialect is now explicitly translated rather than treated as JSON Schema. Support/selection still depends on the configured upstream.

Typed `responseSchema` source support includes required native types (names or official numeric enums), nested `properties`/`items`/`anyOf`, `required`, string enums, nullable types, string/array/object limits, finite numeric bounds and descriptive fields. ProtoJSON camel/snake field names and quoted/numeric int64 exponent forms are accepted; int64 limits are parsed exactly without floating-point rounding. Ordinary ProtoJSON null fields remain unset, while `default: null` and `example: null` remain explicit values (`example` becomes a one-element JSON Schema `examples`). Nullable adds null to the explicit type and does not modify other constraints, including enums. Empty native repeated enum/anyOf fields mean unset, not an invented empty constraint. Conflicting field aliases fail.

For a complete source output-schema `propertyOrdering`, OpenAI targets receive `properties` in that order; Claude or partial ordering produces `schema_property_ordering_not_representable` and requires explicit loss consent. Duplicate/unknown ordering names fail. Unknown typed source validation keywords are hard failures even with `AllowLoss`, including nested/null values; JSON Schema-only keywords belong in a JSON Schema source envelope instead. No constraints are deleted to fit the old dialect. Offline wire tests do not prove that a model accepts every Schema keyword or combination.

Explicitly select `GeminiOutputFormat.LegacySchema` to write `responseMimeType` + typed `responseSchema` from an OpenAI or Claude JSON Schema request. The default remains `LegacyJsonSchema`; same-protocol copies do not migrate envelopes. Typed targets accept explicit single types or a type-plus-null union, nested properties/items/anyOf, string enums, required/string/array/object limits and representable numeric bounds. Int64 counts become decimal strings. Numeric bounds must remain the same decimal value when serialized as the upstream double; unrepresentable precision, overflow and underflow fail rather than rounding the constraint. Strict OpenAI text-schema property ordering propagates recursively; Claude and non-strict OpenAI schemas do not gain an invented order guarantee.

`$ref`, `$defs`, closed/schema-constrained `additionalProperties`, const, non-null multi-type unions and other unavailable validation keywords hard-fail even with `AllowLoss`. Explicit `additionalProperties: true` is equivalent to the native open object and needs no target keyword. Empty/non-string/null-containing enums cannot become an unrestricted native enum. One `examples` entry becomes `example`, including null; multiple examples require `schema_examples_not_representable` consent and retain only the first target annotation while the complete source remains in `OriginalData`. This annotation loss never authorizes dropping validation constraints. Native/default JSON Schema paths retain their original constraints.

Gemini function definitions now accept typed `parameters` input through the same reviewed dialect mapper; `parameters` and `parametersJsonSchema` together fail even with `AllowLoss`. Ordinary function parameters do not inherit a text-format output-order guarantee: nonempty typed `propertyOrdering` is diagnosed for every cross-protocol target. Unknown typed constraints still hard-fail before HTTP. Complete request snapshots and public SDK tests preserve the function schema, choice, call IDs, argument JSON and associated result history; replay back to Gemini uses unchanged JSON Schema under `parametersJsonSchema`. Native Gemini copies preserve the original typed definition.

For typed function targets, explicitly choose `GeminiFunctionSchemaFormat.LegacySchema`; the default `JsonSchema` writes `parametersJsonSchema` unchanged. Function dialect selection is independent of all three text output-format choices and uses the same constraint/numeric checks as typed output schemas. The SDK never invents text-schema property-order guarantees for function arguments. Final-wire snapshots verify complete tool history and call/result IDs in every source direction, and default/native copies remain unchanged.

No JSON Schema constraints are deleted or translated into descriptions, including nested references, zero/false/null values and extension keywords. Supported keywords/combinations still depend on the actual upstream. For unnamed Claude/Gemini schemas, an OpenAI target requires caller-provided `DefaultSchemaName`. OpenAI wrapper name/description and non-strict enforcement cannot be represented exactly in Claude/Gemini: they produce field diagnostics and require explicit loss consent. Claude has no equivalent generic OpenAI JSON-object mode.

```csharp
var options = new LlmConversionOptions
{
    DefaultMaxTokens = 4096,
    DefaultSchemaName = "result",
    GeminiOutputFormat = GeminiOutputFormat.ResponseFormat, // explicit text target
    GeminiFunctionSchemaFormat = GeminiFunctionSchemaFormat.LegacySchema // explicit function target
};
// Add AllowLoss = true only for reviewed metadata/enforcement differences.
// Set TargetThinkingBudgetTokens only for a target supporting manual thinking.
```

Summary/display intent maps to Responses `reasoning.summary`, Claude thinking `display`, and Gemini `includeThoughts` where possible. Request-setting support does not authorize conversion of signed thinking or encrypted reasoning returned by the model; those responses still require native APIs. Unsupported summary detail levels and unknown nested settings remain diagnosed. All options and the conversion algorithm version participate in Session binding identity; changed options invalidate old bindings. This checkpoint uses `conversion-v13`; Sessions from the previous conversion algorithm are rejected rather than silently rebound. Native-provider Session identities are not changed by this conversion-package version.

## Explicit free-text custom tools

Set `EnableCustomToolConversion = true` to encode Responses `custom` tools as ordinary functions on Chat, Claude or Gemini targets. The default remains disabled. Complete calls and streams are supported through the matching request context. Ordinary functions remain separate even when their original names equal a custom tool name.

Each request gets an immutable `LlmConversionContext` with bounded, collision-checked generated names. An ordinary function with the same original name remains distinct. The function schema is a closed object with one required string property, `input`. Text is not parsed as code/JSON, trimmed or executed. No strict-enforcement flag is invented; model adherence remains supplier-dependent. The decoder rejects missing/non-string input and extra properties.

```csharp
var request = LlmProtocolConverter.ConvertRequest(originalResponsesJson,
    LlmProtocol.Responses, LlmProtocol.ClaudeMessages,
    new LlmConversionOptions { EnableCustomToolConversion = true, DefaultMaxTokens = 512 });
// Send request.Data through your native adapter, then use the matching context:
var response = LlmProtocolConverter.ConvertResponse(upstreamJson, request.Context!);
// ConvertingLlmProvider keeps the context automatically for this one call/stream.
// For manually sent streams, create a separate state machine for each enumeration:
var stream = new LlmProtocolStreamConverter(request.Context!);
// Call Push(nativeEvent) for each event and Complete() only after successful EOF.
```

Restoration returns `custom_tool_call` with the original name, exact text and call ID. Stateless response/stream APIs never infer custom type from a name prefix. A context mismatch or historical call-ID reuse fails. Concurrent calls share no restoration map; there is no global/Session map or tool loop. The caller controls context lifetime; it contains request-specific tool names and IDs. A context is immutable and can create independent stream converters; each converter has its own single-use state.

Custom stream items use empty `input` at start, then `response.custom_tool_call_input.delta` and `.done`, item done and a matching final response snapshot. Function argument fragments are buffered until a complete wrapper can be validated; the decoded text is emitted as one input delta. Text output stays incremental. No encoded tool name or wrapper JSON is exposed as a custom input delta. Source protocol validation still runs on the original encoded document. Missing source terminal, invalid wrapper, duplicate/wrong IDs, errors or unsupported interleaving fail without a successful terminal. Explicit length/filter finishes retain `response.incomplete` and incomplete item status. Callers must consume the entire stream; a late failure can follow earlier deltas.

History replay preserves custom/function result kinds and consumes each ID once. Completed source item ID/status/`created_by` stay in `OriginalData`; foreign item IDs do not become another supplier's item identity. New local response item IDs and completed/incomplete status remain present. Custom results must be strings: Chat/Claude carry exact text, while Gemini receives the explicitly opted-in object `{"output": text}`. Ordinary result rules stay unchanged; rich arrays are pending.

Only omitted `format` or `{"type":"text"}` is supported. Namespaces require the independent option below. Grammar/CFG, program callers, async/deferred execution, mixed hosted tools and unknown custom fields hard-fail even with `AllowLoss`. Native calls and same-protocol copies preserve all original structures.

Defaults bound the combined custom/ordinary name map to 128 entries, historical calls to 4096, returned calls to 128 and each decoded custom input/result to 1 MiB of strict UTF-8. Positive `MaxToolConversionMappings`, `MaxToolConversionHistoryCalls`, `MaxToolConversionOutputCalls` and `MaxCustomToolInputBytes` override these limits and enter binding identity. With an active custom context, each source-stream argument buffer is limited to `6 * MaxCustomToolInputBytes + 64` UTF-8 bytes, including ordinary functions in that stream; this allows JSON escaping and wrapper overhead without accepting unbounded padding. Counts are checked before aggregation, not just at EOF. Oversized/invalid text fails without truncation. Unmapped native/stateless streams keep their existing behavior. Typed Gemini function targets still reject unrepresentable closed-object constraints; use the default JSON Schema function target for this wrapper. Independent complete/stream goldens, public SDK replay/concurrency and rejection tests run offline; no live custom-tool verification was performed. See [OpenAI custom tools](https://developers.openai.com/api/docs/guides/function-calling#custom-tools) and [custom input events](https://developers.openai.com/api/reference/resources/responses/streaming-events#response.custom_tool_call_input.delta) for native contracts.

## Explicit namespace tool identity

Set `EnableNamespacedToolConversion = true` for Responses namespace tools on Chat/Claude/Gemini targets. This is independent of `EnableCustomToolConversion`: namespaced ordinary functions need only the namespace option; namespaced custom text tools need both. Native calls and same-protocol copies remain unchanged. Cross-protocol namespace data without its opt-in hard-fails even with `AllowLoss`.

```csharp
var options = new LlmConversionOptions
{
    EnableNamespacedToolConversion = true,
    EnableCustomToolConversion = true, // only when custom text tools are present
    DefaultMaxTokens = 512
};
// Use ConvertingLlmProvider, or the same request.Context complete/stream APIs above.
```

The request-local identity is `(namespace, name, kind)`, with a distinct root scope. Generated function names never concatenate/truncate the original identity or infer scope from a prefix. A root `crm__lookup`, `crm`/`lookup`, another namespace's `lookup`, and same-name function/custom tools remain distinct. Responses restore separate `namespace` and `name` fields and the original call ID. Ordinary function parameters and JSON Schema constraints retain their existing behavior; namespace functions are not decoded as custom wrappers. The model sees flattened generated names, so identity restoration does not promise identical model choices or preserve native grouping behavior.

Nonempty namespace `description` has no equivalent group field in these targets. Strict mode fails with `namespace_description_not_representable` at the original description path. Only explicit `AllowLoss = true` permits omitting that group description, with the diagnostic and complete source retained; the SDK never silently concatenates it into function descriptions. Empty descriptions carry no group guidance. Namespace name/description/tools are validated, duplicate group names or duplicate same-kind child definitions fail, and nested namespaces, deferred/program/async tools and unknown fields remain unsupported.

Global `auto`/`required`/`none` modes retain the complete tool set. A named choice can select an unambiguous declared root tool. The official named-choice structures currently expose name/type without a namespace selector: this converter rejects namespaced selectors or names matching a namespaced child instead of guessing a qualified spelling or resolving a collision. `allowed_tools` selection remains outside this slice. Native JSON APIs preserve those structures for callers using the actual upstream contract.

History calls keep their original IDs and distinct namespaces, including tools no longer in current definitions. A result consumes one pending call ID exactly once; function-result `name` and `namespace` are optional consistency checks, never fallback lookup keys. Mismatched metadata, duplicate/orphan results, wrong kind and historical ID reuse fail even with loss consent. Custom results still follow their string-only contract and do not gain invented namespace metadata fields. Completed item metadata stays in `OriginalData`, and the caller's history is never mutated.

`Context.MappedToolCount` counts each encoded identity once; `NamespacedToolCount` and `CustomToolCount` describe overlapping subsets. `MaxToolConversionMappings` bounds the combined identity table (including root ordinary names) and separately the declared namespace-group table, so empty groups cannot evade the limit. Historical/returned-call and raw stream argument bounds apply to any active mapped context, including namespace-only functions; the raw buffer cap still derives from `MaxCustomToolInputBytes`. Contexts remain immutable and request-local. No global registry, namespace authorization policy, tool executor or gateway service is added. Independent complete/17-event goldens and public SDK HTTP/SSE/replay/concurrency tests run offline; no live namespace conversion was verified. Contracts were checked against [namespace definitions](https://developers.openai.com/api/docs/guides/function-calling#defining-namespaces) and the [Responses reference](https://developers.openai.com/api/reference/typescript/resources/responses).

## Explicit hosted search request configuration

`HostedSearchTarget` declares the native search tool of the fixed converting target. `None` disables cross-protocol search by default, including when ordinary loss is allowed. Supported declarations are `ResponsesWebSearch` (`web_search`), `ClaudeWebSearch20250305` (classic direct search) and `GeminiGoogleSearch` (`googleSearch`). The actual target protocol must match. No model-name lookup, automatic version upgrade, SDK query execution, separate search service or retry is added.

```csharp
var options = new LlmConversionOptions
{
    HostedSearchTarget = LlmHostedSearchTarget.ClaudeWebSearch20250305,
    DefaultMaxTokens = 512
};
// Explicit declaration of your configured target, not certification of model/account access.
// Set AllowHostedSearchWithFunctionTools only after verifying that target's combination support.
```

This checkpoint implements **request configuration and output boundaries**, not full hosted-search conversation conversion. The six directions between Responses, classic Claude and Gemini have independent complete request goldens and public HTTP/SSE tests. Their substitute replies contain ordinary text; these tests do not prove that a model searches, accepts a setting, or returns correctly mapped sources/citations. Native APIs already carry complete provider tools and results. Use native Claude for conversations requiring encrypted search result replay.

One search declaration is supported per request. Responses GA `web_search` and its documented dated alias, Claude classic `web_search_20250305` with default/direct callers, and Gemini `googleSearch` with default or explicit text-only `webSearch` are recognized. Native/same-protocol calls preserve all versions and unknown fields. Preview/Chat-specific search models, Claude dynamic code filtering, image search, legacy Google retrieval and additional server tools remain native-only in this slice.

Bare DNS allow-domain lists and explicit approximate city/region/country/timezone transfer between Responses and classic Claude without normalization or inferred locations. Unknown settings, null/empty restrictive lists, paths/wildcards/IP filters, unsupported domain/time restrictions, offline-only access, search call limits (including zero/null presence) and unverified parallel controls hard-fail even with `AllowLoss`. Google Search has no corresponding fields for these source constraints in this mapping. Explicit live access permission uses the peer's native live-search availability; this is not a freshness guarantee. Missing location/settings retain provider defaults, not a fabricated common default.

`search_context_size` has no equivalent Claude/Gemini field. Explicit `AllowLoss = true` can omit it with `search_context_size_not_representable` and the complete original request retained; the SDK never turns it into token or search-count ratios. Other search access/scope/count constraints are not ordinary annotation losses. Generic `TargetSettings` checks still run on the final native request.

Responses and classic Claude global auto/required/none and specific native search choices map where represented. Definitions are retained for `none`. Gemini function-calling modes cannot force or disable Google Search: unsupported trigger choices fail rather than using `functionCallingConfig` or deleting tools. Only omitted/auto intent maps to Gemini in this slice. Function mixing requires the separate `AllowHostedSearchWithFunctionTools` declaration, and native name collisions still fail. Namespace/custom options remain independent; hosted tools never enter their function identity table.

Known search execution, error, citation and generateContent `groundingMetadata` receive specific diagnostics in complete/history/stream conversion. Default strict mode rejects unrepresented business output. Ordinary loss consent retains complete source JSON for complete conversions, while stream diagnostics accumulate under the existing per-event metadata contract; it does not make these results lossless or guarantee that every unsupported search transcript can complete. **Claude search result references, encrypted search content and encrypted citation indices remain hard native-only boundaries**, even with loss consent. This guard checks native content positions, not arbitrary function arguments or user JSON. Full search result/citation/offset mapping and additional trigger semantics are separate pending checkpoints.

Contracts checked: [OpenAI web search](https://developers.openai.com/api/docs/guides/tools-web-search), [Claude search tools](https://platform.claude.com/docs/en/agents-and-tools/tool-use/web-search-tool), and [Gemini generateContent GoogleSearch](https://ai.google.dev/api/generate-content#GoogleSearch). No live hosted search verification was performed.

## Caller-declared target settings

Optional `TargetSettings` declares the support of the configured converting target. It checks the **actual native request after conversion**, including caller-selected budget replacements and defaults. The SDK does not query a model catalog, infer support from names, adjust tokens or remove rejected fields. Declarations restrict implemented mappings; `true` never enables an otherwise unsupported protocol feature. Violations produce field-level `LlmConversionException` diagnostics even with `AllowLoss = true`.

```csharp
var options = new LlmConversionOptions
{
    TargetSettings = new LlmConversionTargetSettings
    {
        AllowedReasoningEfforts = ["low", "medium", "high"],
        AllowedOutputFormats = ["text", "json_schema"],
        MaximumOutputTokens = 4096,
        MaximumThinkingBudgetTokens = 2048,
        SupportsSampling = false
    }
};
// Example caller declaration, not an SDK claim about a named model/account.
```

`null` lists leave that dimension undeclared; an empty list rejects every explicit value. Efforts refer to the emitted native effort/level field, including `none`/null when emitted. `AllowedThinkingModes` separately checks Claude/Gemini native `manual`, `adaptive`, and `disabled` controls. Positive manual budgets receive optional minimum/maximum checks; zero/off and -1/dynamic are never clamped into that range. `AllowedOutputFormats` checks text/JSON-object/JSON-Schema intent in every supported envelope. Optional sampling, parallel-tool-control and reasoning-output-control flags reject field presence when false, including zero, false and null. Omission does not invent a field or a default capability.

`MaximumOutputTokens` validates an explicitly emitted output-limit argument, including `DefaultMaxTokens` written for Claude. If an argument is omitted, it remains omitted; this does not bound the upstream default, returned usage or billing. These declarations apply only to cross-protocol requests, never to response/event content, same-protocol copies or native provider calls. Use separate converting provider IDs/targets when models need different declarations; this is not a gateway policy engine or an entitlement check.

Registration captures independent read-only list snapshots. Lists use set semantics (ordinal sort/deduplication), so reordering equivalent declarations does not change binding identity. Mutating caller-owned lists after registration cannot change active behavior or identity; intentionally changed declarations invalidate the old converted Session. The pure request API captures a fresh declaration snapshot per call. No policy adds retries, target changes, tool execution or background work.

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
