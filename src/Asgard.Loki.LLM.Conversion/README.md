# Asgard.Loki.LLM.Conversion

The matrix below covers Chat Completions, Responses, Claude Messages and Gemini. The separate [Nova ↔ Converse complete JSON pair](#explicit-nova--converse-complete-json) uses its own Bedrock decorator and does not expand this four-protocol matrix. Native Ollama Chat/generate have separate protocols/clients and are outside this conversion matrix; neither implicitly becomes OpenAI Chat. Native Ollama tool/history, thinking, schema and prompt/context settings can be passed through their own clients without invoking this package. Tests cover both native protocols with/without loss consent across every supported peer, both directions, same-protocol copies and request/response/stream entry points; enum expansion cannot enable unimplemented conversion.

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
| Content | Ordered user/assistant text, system instructions, inline Base64 images; user PDF inputs across all twelve directions; declared UTF-8 text files across six Responses/Claude/Gemini directions |
| Functions | JSON Schema or Gemini typed `parameters` input; explicit typed target parameters; named/auto/required/none choice, original call IDs, arguments and associated results |
| Tool results | Text arrays between Chat/Responses/Claude; images/PDF arrays between Responses/Claude; original string/array kind, order and call IDs retained |
| Custom tools | Opt-in Responses free-text custom → Chat/Claude/Gemini functions, complete/stream restoration and history replay |
| Namespace tools | Separate opt-in flattens Responses namespace functions/custom tools; restores namespace/name/kind through complete calls, streams and replay; group descriptions require diagnosed loss consent |
| Hosted search | Six Responses / classic Claude / Gemini request directions, plus four Chat ↔ Responses/Claude directions preserving required search; complete Chat ↔ Responses URL citations; complete Gemini Web grounding ↔ Chat/Responses with declared endpoint index units; opt-in single-text-part Responses ↔ Gemini citation streams, plus explicit ordered Responses text-part concatenation; remaining result/citation diagnostics and hard native-only encrypted search state |
| Gemini results | Ordinary functions require JSON object text. Opt-in custom conversion explicitly wraps its arbitrary text result as `{"output": text}` |
| Parameters | Temperature, top-p, explicit output limits; top-k and stop where the destination supports them; Gemini candidateCount → Chat n for complete calls, single-candidate streams and explicitly enabled multi-candidate streams |
| Structured output | JSON Schema wrappers across all four protocols; JSON Schema bodies copied unchanged by default. Gemini `responseJsonSchema`, modern `responseFormat.text` and typed `responseSchema` input supported. Explicit `LegacySchema` writes representable typed output schemas. Plain JSON/text modes map between OpenAI and Gemini |
| Reasoning settings | Qualitative effort, explicit Claude/Gemini budget or dynamic/off mode, and summary/display intent where representable; no model-name heuristics or calculated budgets |
| Parallel functions | Chat/Responses `parallel_tool_calls` ↔ Claude `disable_parallel_tool_use`; Gemini has no equivalent switch and produces a diagnostic |
| Responses | Creation in the common stateless subset; no cross-protocol `previous_response_id`, `store` or background semantics |
| Responses output | Single candidate, ordinary text/function calls and supported finish reasons |
| Complete Chat ↔ Gemini candidates | All original alternatives in source order, exact nonnegative int32 indices, independent ordinary tools/finishes/declared-unit citations and one response-wide usage; complete replies only |
| Usage | Base totals, cache reads and reasoning counts with native accounting conventions; OpenAI details transfer between Chat/Responses. Claude cache-write breakdown or unsupported nonzero details diagnose loss |
| Streaming | Immediate text deltas; function arguments buffered until complete JSON; correct target start/item/delta/done/terminal sequence |
| Diagnostics | Unknown fields, hosted tools, unsupported media/settings and unrepresentable usage details are rejected by default |
| Opaque reasoning/signatures | Cross-protocol conversion always rejects these, even with `AllowLoss`; use native APIs |

Encrypted Responses `compaction` items are also a hard native-only boundary in request, response and stream conversion, including when ordinary loss is allowed. Same-protocol copies preserve them unchanged. `ConvertingLlmProvider` does not implement or advertise the optional native compaction operation; configure a separate native target for it.

Streams are single-call state machines, not resumable event subscriptions. Missing terminal events, invalid/incomplete tool arguments, inconsistent final Responses output, unsupported interleaving or upstream errors fail explicitly. A late failure may occur after earlier deltas have been returned; callers must observe the full enumeration. No successful terminal is emitted for a truncated source stream. Direct same-protocol stream copies leave native transport validation to the provider.

### Complete Gemini ↔ Chat candidate arrays

`LlmProtocolConverter.ConvertResponse` retains every complete candidate/choice in both Gemini → Chat and Chat → Gemini directions. Existing fixed-Gemini/fixed-Chat `ConvertingLlmProvider` entries compose the corresponding calls without a new public type. Original sparse/nonzero indices and array order remain unchanged. An omitted native Gemini Proto3 index is zero; an omitted index in a legacy single Chat choice retains its existing zero behavior, while multiple Chat choices require explicit indices. Explicit null, negative, fractional/out-of-range and duplicate indices fail even with `AllowLoss`.

Each alternative has separate function-call association, text, finish and grounding tables. A tool call in one alternative cannot change another alternative's finish; `MAX_TOKENS`/`length` stay incomplete even with tools. Equal tool IDs in different alternatives are not concatenated into one call; callers select an alternative before replaying history. Existing strict argument, opaque-state, field-loss and declared citation-unit rules apply per alternative. Empty text is retained. Diagnostics identify original array positions, not sparse native indices. The complete source remains in `OriginalData` / `Metadata.Conversion.UpstreamData`; shared usage is converted once rather than summed over alternatives. An invalid later alternative rejects the complete conversion.

Loki does not copy the former's shared tool-finish flag or the latter's empty-text omission. Ten independent complete source/target goldens cover both directions, including same-name tools, exact large-integer arguments, Unicode citations, empty/filter/length outcomes and snapshots. These are offline protocol checks, not live model acceptance.

### Gemini candidate-count requests over Chat

`generationConfig.candidateCount` maps to Chat `n`. This direction is supported by the existing request converter and fixed-Chat public SDK. Explicit counts must be positive int32 JSON numbers. Missing counts stay missing; null, strings, booleans, fractions and out-of-range values fail even with loss consent. The SDK does not infer model limits, clamp the count, alter reasoning/output limits or remove Schema constraints. Actual model acceptance remains the caller's responsibility.

Two independently authored complete request and structured/plain reply goldens plus tests cover this implementation and reverse complete conversion. Final HTTP retains count, high reasoning effort, strict Schema, null/false and an exact integer above the JavaScript safe range. Per-call count changes preserve the existing complete-target Session. Original requests and upstream replies remain available independently. There are no new public contracts, package dependencies or shared coordinator; previously supported index-zero behavior and `conversion-v25` binding identities remain unchanged.

By default, counts above one with streaming fail before native acquisition/HTTP, including direct adapter use with a missing/false `stream` field. Count one retains the existing single-candidate lifecycle. The explicit multi-candidate stream profile below enables the documented Chat/Gemini subset. Chat `n` → Gemini `candidateCount` requests remain unsupported. Complete multi-candidate mappings to Responses/Claude also remain unsupported; the SDK does not select candidate zero or merge alternatives to approximate them. Native identity calls/copies retain their existing extension surfaces.

### Explicit Chat ↔ Gemini multi-candidate streams

`EnableMultipleCandidateStreams = true` enables ordinary text/function streams between Chat and Gemini in the existing `LlmProtocolStreamConverter` and fixed-upstream `ConvertingLlmProvider`. The default remains the existing single-candidate mode. Pass the same explicit policy to the provider when exposing Gemini streams over native Chat; `generationConfig.candidateCount` then maps to `n` without the earlier default rejection. A requested count exceeding the configured receiving bound fails before HTTP. The opposite request `n` mapping is still unsupported.

```csharp
var policy = new LlmConversionOptions
{
    EnableMultipleCandidateStreams = true,
    MaxStreamCandidates = 8
};
var converter = new LlmProtocolStreamConverter(
    LlmProtocol.ChatCompletions, LlmProtocol.Gemini, policy);
// Push original JSON events in arrival order. Call Complete only after normal
// upstream enumeration, disposal and the final cancellation check succeed.
```

A dictionary preserves original nonnegative int32 candidate indices, including sparse indices; no indexed array is allocated. Gemini's omitted native index is zero, while Chat indices must be explicit. A frame cannot repeat an index. All candidates share native root identity validation and one target stream identity. Functions, names, arguments and finishes belong to their own candidate. Equal tool IDs in different alternatives remain separate calls. Fragmented Chat names/arguments are buffered until complete valid JSON is available; duplicate JSON properties, invalid Unicode, changed IDs and malformed arguments fail. Native atomic Gemini functions may be emitted immediately. Unsupported Gemini partial-function/opaque/signed dialects stay native boundaries.

Immediate deltas follow native frame/candidate order. At `Complete`, every observed candidate must have a valid native terminal before any target finish is returned. Buffered tools and target finishes are finalized in first-seen candidate order. A stopped candidate cannot reopen content or repeat its finish, while other candidates continue. All final events remain held through the upstream provider's EOF/disposal/final cancellation; errors, truncation and native Gemini prompt blocking cannot release successful finishes. Callers must observe the complete enumeration. The converter neither generates SSE `[DONE]` nor executes tools.

Usage is read once from the shared native envelope and emitted at most once in a final **usage-only event** (`choices: []` or `candidates: []`), after all independent finishes. Unknown counters remain absent rather than invented as zero; explicit zeros stay zero. Unrepresentable usage details require diagnostics/loss consent. Native grounding keeps source integrity checks and explicit loss diagnostics; this profile does not add Chat citation deltas, general Gemini citation mapping or support for multiple candidates in Responses/Claude. Existing citation profiles retain their own protocol/layout boundaries.

Every result keeps its exact triggering original source JSON. Completion results use the last native JSON snapshot, which is not a full transcript archive. Diagnostics prefixed `$candidate[n]` identify a **logical native candidate key**, not a source array position. Common-envelope diagnostics and usage are not multiplied by candidate count. Default/new-field serialization preserves the earlier `conversion-v25` bytes; explicitly enabling this policy or setting its limits changes the Session binding. Native identity copies are unaffected.

| Policy | Effective default | Meaning |
| --- | --- | --- |
| `MaxStreamCandidates` | 128 | Distinct observed native candidates; explicit request counts are checked before HTTP |
| `MaxCandidateStreamInputBytes` | 16 MiB | Cumulative compact logical-source JSON serialized to UTF-8, across all candidates |
| `MaxCandidateStreamOutputBytes` | 4 MiB | One Push/Complete return batch: every target JSON plus every independently retained original source snapshot |
| `MaxCandidateStreamDiagnostics` | 1024 | Cumulative common-envelope and candidate diagnostics |

Nullable omitted limits use these defaults and do not change default option bytes. Explicit limits must be positive. Existing `MaxStreamAnnotations`/`MaxStreamAnnotationBytes` additionally bound native grounding entries across all candidates together. These are SDK state/serialization bounds, not SSE wire lengths, provider quotas or billing rules. Overflows fail even with `AllowLoss`; no candidate, text, parameter, metadata or diagnostic is trimmed.

Five independently authored complete source/target event transcripts and tests cover both directions, final HTTP, sparse/omitted indices, same-ID alternatives, interleaved names/arguments, exact numbers, empty/filter/length results, shared usage, resource boundaries, source snapshots, mode/policy Session changes and EOF/read/disposal/cancellation failures. These tests establish SDK protocol behavior, not live model acceptance or complete native-feature parity.

### Source annotation stream integrity

Responses `response.output_text.annotation.added` is recognized and validated against its original item/output/content/annotation indices. URL citation ranges are checked against the completed native text using the declared `OpenAiCitationIndexUnit`; unspecified units retain only the existing UTF-8 plausibility bound and are not guessed. Annotations may follow text.done or arrive out of annotation-index order, but cannot attach to a tool, reuse an index or arrive after the part/item/response has closed. Initial snapshots and the first authoritative done/final snapshot may introduce annotations; observed entries cannot disappear or change. Every subsequent part/item/final snapshot must match the full raw annotation list, including unknown fields, false/null and array order.

`AllowLoss` still governs unrepresentable target citation fields. It cannot bypass invalid source associations, changed snapshots, invalid ranges, missing terminal events or resource limits. This validation does **not** establish Chat citation deltas or broader Gemini streaming grounding support; strict conversion retains those boundaries. Valid consented-loss streams retain their normal text/tool/usage behavior and available upstream metadata. Nullable native annotation events remain distinct from a missing property; unsupported annotation kinds still produce diagnostics.

`MaxStreamAnnotations` defaults to 1024 unique annotations per converted Responses stream. `MaxStreamAnnotationBytes` defaults to 1 MiB of retained annotation JSON serialized to UTF-8; this is an SDK state bound, not a claim about original wire encoding or a provider maximum. All parts share one budget. Repeated done snapshots of the same entries are not charged again, while snapshot-only entries are charged. Sparse indices use dictionaries rather than allocating indexed arrays. Exceeding either limit fails; the SDK never trims annotations to fit. Both positive scalar options are snapshotted and enter Session binding. Native identity copies remain unfiltered by these conversion state limits.

An independently authored two-part Unicode source and six-event Chat golden, plus public SDK/fragmented-SSE, failure, resource, immutability and binding regressions cover this implementation. They are offline protocol checks, not live search or an assertion that every cited claim is accurate. Contract reference: [Responses annotation-added event](https://developers.openai.com/api/reference/resources/responses/streaming-events#response.output_text.annotation.added).

### Gemini stream grounding source integrity

The [Gemini GroundingMetadata/Support contract](https://ai.google.dev/api/generate-content#GroundingMetadata) specifies incremental new groundingChunks and support indices across all streaming responses. The converter appends original chunk objects in order instead of treating every frame as a replacement source table. Duplicate URLs remain separate entries. It retains bounded, independently copied source/support JSON, including unknown fields, and validates explicit native int32 indices, object/array containers, known source oneof conflicts and structurally ordered segment endpoints. Null/scalar entries cannot be skipped to shift source indices. Pending references may resolve from a later frame; at normal completion every referenced index must exist. A very large index never causes an indexed-array allocation.

The same `MaxStreamAnnotations` and `MaxStreamAnnotationBytes` bound the total retained Gemini chunks **plus supports** for one converted call (1024 items and 1 MiB by default). Byte accounting uses retained item JSON serialized to UTF-8, not original SSE bytes or a provider quota. Source/response metadata frames are not deduplicated by URL. Overflows and unresolved/invalid references are hard failures even with AllowLoss. Default source integrity alone does not establish byte/text/part association. The explicit single-text-part Gemini → Responses profile below adds exact mapping; other layouts/pairs and confidence/presentation equivalents remain separate work. No text search, guessed part identity, source renumbering or invented search execution is added.

After a candidate finish, a Gemini candidate containing only index/groundingMetadata may still append metadata within that same call; it cannot reopen text/tools or repeat the finish marker. The target terminal remains delayed until the source finishes normally and pending indices resolve. Usage-only tails remain supported. Gemini tails admit only declared identity/candidate/usage root fields, so blocking feedback cannot be hidden inside a metadata or usage tail. Native errors, truncation or invalid metadata tails cannot first publish a successful target finish.

With the default `Disabled` mode this remains **source integrity only**. Default strict conversion still rejects unrepresented grounding. Caller-selected AllowLoss retains explicit grounding diagnostics and may emit the existing text/usage subset. Metadata-only source frames do not necessarily produce a target event; native protocols/copies remain the path for retaining every original frame. Standard [Chat completion chunk delta fields](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events#chat.completion.chunk) do not declare annotations, so this SDK does not invent delta.annotations. Complete-response citation mappings are unchanged.

Tests use authored full source events, a full native Gemini request and an independent consented-loss Chat sequence. They cover global/source/segment index structure, duplicate URLs, pending references, metadata tails, hard state bounds, mutation, no premature finish/retry and a real old-version Session rejection. Fixtures are protocol examples, including defensive pending-reference ordering, not real Google Search output or quality evidence. That grounding contract used `conversion-v23`; the current algorithm identity is `conversion-v25`; old converting-provider Sessions are rejected before HTTP. Native provider identities and the one-capability dependency boundary remain unchanged.

### Explicit Gemini → Responses citation streams

`GeminiGroundingStreamMode.SingleTextPart` opts into one fixed endpoint convention: text deltas accumulate into **one logical text part**, segment offsets refer to that accumulated part's native UTF-8 bytes, and support records append in arrival order. It additionally requires `OpenAiCitationIndexUnit`; an unspecified unit fails before HTTP. The SDK does not infer this convention from a model name or claim it for all Gemini endpoints. The default mode remains `Disabled`.

```csharp
var options = new LlmConversionOptions
{
    GeminiGroundingStreamMode = GeminiGroundingStreamMode.SingleTextPart,
    OpenAiCitationIndexUnit = LlmCitationIndexUnit.UnicodeScalar
};
// Register these options on a ConvertingLlmProvider fixed to Gemini,
// then consume the public Responses streaming API.
```

Text remains incremental. After the source enumeration and resource disposal succeed, accumulated source/support snapshots map through `GeminiGrounding` and `CitationOffsets`. Every byte boundary, optional exact segment substring, original source index and Web URI is checked. Repeated URLs, overlapping/empty ranges and repeated text retain their original association; no text searching, normalization or source deduplication occurs. Only part zero is representable in this declared profile. Multiple parts, non-text content/tool interleaving and nonempty `renderedParts` are hard `grounding_stream_layout_not_representable` failures even with `AllowLoss`. Ordinary streams without grounding and native identity copies retain their existing behavior.

The target emits native `response.output_text.annotation.added` with stable item/output/content/annotation indices and sequence numbers, then text.done, part.done, item.done and the response terminal. Part/item/final snapshots contain the same complete flat URL annotation list. MAX_TOKENS/SAFETY preserve `response.incomplete`; malformed metadata, unresolved sources, truncation, tail/read/disposal failure or cancellation cannot first publish validated-looking annotations or completion. Cancellation between final events also suppresses subsequent delivery. Direct users of `LlmProtocolStreamConverter` must call `Complete` only after normal source completion.

The configured count/serialized UTF-8 byte limits apply independently to retained source chunks plus supports, and to expanded output annotations. Many source references cannot bypass limits by expanding into an unbounded target list. Empty known `confidenceScores`/`renderedParts` arrays are equivalent to omission and remain intact in original JSON; nonempty confidence and query/display/unknown metadata still require diagnosed loss consent. Private sources and opaque replay state retain their existing boundaries. No search execution, source download or tool result is invented.

Independent fixtures include independently authored full source/native request and fifteen-event Responses golden, hand-calculated scalar/UTF-16/UTF-8 annotation lists, independent Responses source-lifecycle verification, mutation/budget/layout/field regressions and tests including EOF cancellation and cancellation between final events. A changed conversion policy invalidates incompatible converting-provider Sessions. Other citation-stream pairs/layouts, Claude citations and citation history remain separate work. These fixtures verify protocol behavior, not real Google Search or model acceptance. Contracts: [Google grounding/segments](https://ai.google.dev/api/generate-content#GroundingMetadata), [OpenAI annotation-added event](https://developers.openai.com/api/reference/resources/responses/streaming-events#response.output_text.annotation.added).

Tool results consume one pending call exactly once. Gemini results without IDs must match a unique pending function name; completed history is excluded, and generated IDs avoid explicit IDs throughout the request. Stream validation checks closed blocks, item identities, sequence numbers, done snapshots, duplicate terminals and mutations after completion. Text cannot overtake an earlier buffered tool. Explicit length/filter finishes retain incomplete Responses item status. Unknown nested fields are diagnosed even inside recognized events. Missing usage remains unknown; conversion to Claude requires final input/output counts rather than inventing them. Claude stream start usage excludes cached input from `input_tokens`.

### Explicit Responses → Gemini citation streams

The same `GeminiGroundingStreamMode.SingleTextPart` declaration enables the reverse Responses → Gemini stream, with `OpenAiCitationIndexUnit` describing the **source** OpenAI endpoint. Register a `ConvertingLlmProvider` fixed to `LlmProtocol.Responses`, then call the public `loki.LLM.Gemini.StreamAsync` API. Default Disabled still rejects or diagnoses omitted citations; missing units fail before HTTP. Same-protocol copies bypass conversion unchanged.

The declared Gemini target accumulates text deltas into one logical text part. Native Responses output/item/content/annotation indices, IDs, sequence numbers, annotation-added events and part/item/final snapshots are validated before the final metadata is produced. URL annotations may arrive after text.done or first appear in an initial/part/item/final snapshot; already observed fields cannot change. After normal upstream EOF/disposal, the final Gemini event contains groundingChunks/groundingSupports, native usage and the finish reason, with empty delta parts to avoid repeating the text. Original URLs/titles, duplicate references, order, empty/overlapping ranges and exact scalar/UTF-16/UTF-8 boundaries map through the existing complete-response citation writer. No text search, normalization, source deduplication or search-execution record is invented.

Under SingleTextPart, multiple cited text blocks or tool interleaving remain a hard grounding_stream_layout_not_representable boundary even with AllowLoss. The separately selected ConcatenatedTextParts profile below handles sequential pure-text blocks; general multi-part/tool citation streaming and Chat annotation deltas remain unsupported. Streams without citations keep their existing tool/part behavior. Retained source annotations and the expanded output chunks **plus supports** each have independent count/serialized UTF-8 byte limits. AllowLoss cannot bypass these limits, invalid Unicode ranges, mismatched raw snapshots or a missing/late/failed terminal. Explicit max-token/filter termination remains MAX_TOKENS/SAFETY. Unknown annotation fields require diagnosed consent and stay in original JSON.

A full independently authored Responses source and five-event Gemini golden have hand-calculated byte ranges and three source-unit tables; the source reuses an existing independently authored native transcript, never production converter output. Reverse-profile replay checks the final native text/annotations/usage. Public SDK cases cover exact native HTTP body/path/auth, EOF/disposal, incomplete status, early exit, cancellation and late source/transport failures. Algorithm identity is `conversion-v25`; previous converting-provider Sessions, including v24, require a fresh ID or caller-managed expiration. Native provider/core fingerprints and package dependencies are unchanged. No real model/search or new GitHub Actions run is claimed.

### Explicit concatenation of Responses text parts

`GeminiGroundingStreamMode.ConcatenatedTextParts` is a separate opt-in for Responses → Gemini streams. Declare `OpenAiCitationIndexUnit` as before and register a converting provider fixed to Responses, then consume the public Gemini streaming API. Sequential pure-text parts across multiple Responses messages become one cumulative Gemini text part. No separator, normalization or synthetic text is added. Source message/part grouping and native IDs remain in original upstream JSON; the converted target explicitly uses cumulative text grouping and does not claim to recreate the original grouping during reverse replay.

```csharp
var options = new LlmConversionOptions
{
    GeminiGroundingStreamMode = GeminiGroundingStreamMode.ConcatenatedTextParts,
    OpenAiCitationIndexUnit = LlmCitationIndexUnit.UnicodeScalar
};
```

Before rebasing, the source validates raw annotation snapshots and exact output/content part identities against the final native array. It also proves that part starts follow native part order and nonempty text deltas never return to an earlier block; equal/repeated text cannot hide a wrong association. Starting a later empty part early is safe when actual text remains sequential. Tools, reversed native part order and interleaved nonempty text remain hard layout failures even with AllowLoss; nonexistent/relabelled part indices are malformed source data. This profile is not a declaration about Gemini source-part semantics, so cross-protocol use in the opposite direction fails before HTTP. Native identity copies remain unchanged.

The implementation reuses the complete Chat citation mapper's declared-unit prefix rebasing, then the existing Gemini citation writer/Unicode conversion. Non-ASCII and empty prefixes, duplicate URLs/titles, combining characters and empty/overlapping ranges keep exact associations. Source annotations and expanded target chunks plus supports retain independent limits. Incremental text is not withheld for final citation mapping; native EOF/length/disposal/cancellation and all source checks precede grounding/usage/finish delivery. Partial max-token/filter results remain partial; late native/transport errors cannot first release success.

Independent full fixtures contain three 26-event native transcripts with declared units, an eight-event target, complete request/wire snapshots and authored preceding v25 option bytes. Tests cover non-ASCII prefixes, empty parts, duplicate sources, reverse cumulative replay, actual part keys even when texts match, early empty starts, hard layouts/Unicode/state limits, native copies, incomplete status and late read/disposal/cancellation failures. The opt-in uses an existing serialized option field with new enum value 2; Disabled=0 and SingleTextPart=1 and exact preceding default/single-profile v25 bindings remain unchanged. Changing modes invalidates that Session normally; unchanged concatenation settings recover across clients. No live search/model, new dependency, push or GitHub Actions run is claimed.

## Explicit PDF inputs

User-supplied inline PDFs convert between all four request protocols, including requests whose **reply** is consumed over SSE. Ordered text/file blocks, multiple files, unchanged Base64 payloads and existing tool-history associations are preserved. No new package, public file model, resolver, upload operation or SDK file store is added.

| Native protocol | Supported input shape |
| --- | --- |
| Chat Completions | `{"type":"file","file":{"file_data":"data:application/pdf;base64,...","filename":"optional.pdf"}}` |
| Responses | `{"type":"input_file","file_data":"data:application/pdf;base64,...","filename":"optional.pdf"}` |
| Claude Messages | `{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"..."}}` |
| Gemini generateContent | `{"inlineData":{"mimeType":"application/pdf","data":"..."}}` |

OpenAI `filename` transfers between Chat and Responses. Claude/Gemini have no verified equivalent filename field in these structures: `file_name_not_representable` requires explicit `AllowLoss` before omission, with the original retained. Claude document titles/context/citation/cache controls and Responses rendering detail remain diagnosed; a filename never becomes a title or extra instruction. Unknown nested fields, including null/false values, follow the same strict policy.

OpenAI cross-protocol `file_data` requires the explicit data-URL MIME; a bare Base64 value or `.pdf` filename does not provide that declaration. That default slice maps only PDF; the separate opt-in below adds declared UTF-8 text files. Invalid/empty Base64, incompatible MIME, conflicting sources and non-user file roles fail rather than being repaired or moved. Base64 syntax is checked without allocating a full decoded PDF buffer. The SDK does not validate PDF structure, decrypt files, render pages or certify model/account acceptance. Native calls preserve their complete original JSON and supplier-supported file formats.

Claude's native URL-PDF source can map directly to Responses `file_url`, retaining the exact HTTP(S) reference and making no separate SDK download. Generic Responses URLs lack a verified file-type declaration for the peer; other target URL mappings require caller-provided data or a future explicit resolver. Provider `file_id` and Gemini `fileData` references are not assumed portable across configured targets, even with ordinary loss consent (`provider_file_reference`). Same-protocol copies preserve references unchanged. No automatic file upload, target rewrite, content fetch or retry occurs.

This is **request input support**, not generated-file output. Independent full request goldens cover twelve directions and history roundtrips; tests cover final native requests, references failing before HTTP, metadata consent and a URL sent only with the model request. The byte samples are opaque protocol fixtures, not renderable PDFs; substitute text replies do not verify upstream document parsing. The rich-result mapping below reuses these PDF blocks without creating separate user messages. Contracts checked: [OpenAI file inputs](https://developers.openai.com/api/docs/guides/file-inputs), [Claude PDF inputs](https://platform.claude.com/docs/en/build-with-claude/pdf-support), and [Gemini generateContent documents](https://ai.google.dev/gemini-api/docs/generate-content/document-processing). No live file call was made.

## Explicit UTF-8 text file inputs

`TextFileEncoding = LlmTextFileEncoding.Utf8` declares the encoding of caller-supplied `text/plain` documents. The default `Unspecified` fails with `text_file_encoding_required` when such a file needs conversion, even with ordinary `AllowLoss`. It does not sniff bytes, guess from filenames or change provider/model support.

```csharp
var options = new LlmConversionOptions
{
    TextFileEncoding = LlmTextFileEncoding.Utf8,
    MaxTextFileBytes = 1024 * 1024
};
```

| Protocol | Native input shape in this mapping |
| --- | --- |
| Responses | `input_file.file_data = "data:text/plain;base64,..."` |
| Claude Messages | `document.source = {"type":"text","media_type":"text/plain","data":"..."}` |
| Gemini generateContent | `inlineData = {"mimeType":"text/plain","data":"...Base64..."}` |
| Chat Completions | Non-PDF file content parts are unsupported; no file-to-message flattening |

All six directions between Responses, Claude and Gemini retain document/file containers, individual file order, surrounding text and original tool-call/result IDs. Responses/Claude rich tool-result arrays also reuse this mapping without changing their result kind or association. This adds request/history input support, including calls consumed over SSE; generated files and citation replay are separate contracts.

Base64 syntax and decoded size are checked before buffer allocation. UTF-8 decoding and raw-string encoding reject malformed bytes/unpaired surrogates, without replacement, normalization, newline changes or BOM removal/insertion. `MaxTextFileBytes` defaults to 1 MiB of decoded UTF-8 **per file**, not Base64 characters, UTF-16 length, total request bytes or a supplier quota. Exceeding it is a hard failure; no file truncation occurs. The two scalar options are snapshotted and participate in the current `conversion-v23` binding. PDF handling and native identity calls keep their own behavior.

Only explicit `text/plain` is mapped here; charset parameters, `application/json`, other `text/*`, office/binary files, unresolved URLs and provider-owned file IDs do not acquire guessed equivalents. Claude Base64 documents still mean PDF, not a fabricated Base64 plain-text source. User roles remain required. Missing/empty/null data and ambiguous sources fail. File names, Claude title/context/citation/cache settings and future null/false fields retain explicit loss diagnostics; filenames never become titles or instructions. An invalid association, encoding, target container or bound cannot be bypassed by loss consent.

Three independently authored full native requests include two files, Unicode/combining marks, CRLF/NUL/BOM and tool history; six directional goldens and replays verify the entire structure. Tests verify final native requests/auth/paths, declared encoding/size/Chat boundaries and invalid UTF-8 before HTTP. Further cases cover exact decoded-byte limits, invalid raw Unicode, metadata/filename losses, unsupported source/target forms, original JSON and Session changes. Substitute replies establish protocol wiring only; no supplier/document-understanding call, source download or live quality test was performed. Official contracts: [OpenAI file inputs](https://developers.openai.com/api/docs/guides/file-inputs), [Claude plain-text documents](https://platform.claude.com/docs/en/build-with-claude/citations#plain-text-documents), [Gemini generateContent document types](https://ai.google.dev/gemini-api/docs/generate-content/document-processing#document-types).

## Rich tool-result history

Tool results retain their native **string or content-array kind**. Chat tool `content`, Responses `function_call_output.output`, and Claude `tool_result.content` map text arrays across six directions. Responses/Claude additionally map ordered image/PDF arrays in both directions, reusing the image/file validation and metadata/reference boundaries above. Empty arrays and individual empty text blocks remain arrays/blocks; JSON-looking text is not parsed, concatenated or stringified as another result kind.

Original call IDs are consumed exactly once. Same-name parallel calls remain distinct and reverse-order results keep their original links. Consecutive Responses function calls are encoded together in one Chat assistant `tool_calls` turn. Ordinary messages or a new tool turn before pending results have completed fail with `tool_turn_order_not_representable`; the SDK does not reorder history or invent missing results. Ordinary user text following all completed results remains representable.

By default, explicit Claude `is_error: true` and Gemini root `functionResponse.response.error` are hard `tool_result_error_not_representable` boundaries for other protocols, even with `AllowLoss`: an error cannot become ordinary successful text. The explicit Claude/Gemini error formats below add narrowly defined equivalents. Explicit Claude false is equivalent to its normal default and remains in original source JSON. Invalid/null error flags fail. Known encrypted citations inside native Claude tool-result text also remain hard native-only state; ordinary JSON-looking text and function arguments are not recursively inspected.

Chat tool-result arrays contain text only. Gemini's object response is not assumed equivalent to any of these arrays, including an empty array: unsupported targets fail with `rich_tool_result_not_representable` rather than receiving a guessed wrapper. Native same-protocol JSON remains unchanged. Gemini multipart result mapping remains separate work; the explicit error formats below do not claim media-result/error arrays, tool-result output deltas or a tool executor.

With the existing custom conversion opt-in, Responses custom text-result arrays map to Chat/Claude; image/PDF custom arrays map to Claude. They use the same request-local kind/name/ID context. Gemini custom strings keep the existing explicit `{"output": text}` envelope; custom arrays do not acquire that envelope. `MaxCustomToolInputBytes` also bounds each custom source array's serialized JSON UTF-8 bytes. Raw strings/keys are checked before serialization so invalid Unicode cannot become replacement characters; a size lower-bound check rejects obviously oversized data before full JSON serialization. Native references and unknown fields follow the same rules as ordinary rich results.

Tests include eight full authored request/roundtrip goldens, tests, namespace/custom identity, exact custom-array bounds, invalid Unicode, error/opaque/reference boundaries, parallel reverse-order association and Chat turn ordering. HTTP fixtures return ordinary text: no tool, download, image/PDF parser or live model was executed. Contracts checked: [Chat tool-message content](https://developers.openai.com/api/reference/typescript/resources/chat/subresources/completions), [Responses function output](https://developers.openai.com/api/reference/typescript/resources/responses#response-function-tool-call-output-item), and [Claude tool-result types](https://github.com/anthropics/anthropic-sdk-python/blob/main/src/anthropic/types/tool_result_block_param.py).

## Explicit Claude/Gemini tool errors

`LlmConversionOptions.ToolErrorFormat` declares the **request-history** convention for synchronous Claude/Gemini tool errors. The default `LlmToolErrorFormat.NativeOnly` retains strict rejection; `AllowLoss` cannot select a convention or turn errors into success. Chat/Responses have no equivalent native failure flag and remain unsupported destinations for these errors. Native provider calls and same-protocol copies retain original JSON without applying this mapping.

| Explicit format | Claude `tool_result` | Gemini `functionResponse.response` |
| --- | --- | --- |
| `GeminiResponseObject` | `is_error:true`, string content containing the complete JSON response object | Original complete object with a non-null root `error`; sibling `output`, unknown members, arrays, exact numbers, false/zero/empty values stay intact |
| `GeminiErrorText` | `is_error:true`, original string content | Exactly one string member: `{"error": text}`; empty and JSON-looking strings remain text |

The object format does not unwrap or stringify individual business values: the existing Claude string representation holds the entire native response object, which must parse as one strict JSON object on replay. Missing/null `error`, malformed/duplicate JSON and mismatched content kinds fail. The text format never guesses JSON from a string, and rejects extra response members or non-string error values rather than dropping them. Gemini error results with `parts`, `willContinue` or `scheduling` hard-fail even with loss consent; these synchronous profiles cannot preserve media, continued results or asynchronous execution. Claude rich-content arrays remain unsupported errors under both formats; Gemini JSON error arrays remain business JSON inside the complete-object format and do not become Claude media blocks.

```csharp
var conversion = new LlmConversionOptions
{
    ToolErrorFormat = LlmToolErrorFormat.GeminiResponseObject,
    MaxToolErrorBytes = 1024 * 1024
};
// Choose GeminiErrorText instead only when the application uses the text convention.
```

Success results are not implicitly upgraded to Gemini failures: ordinary Chat/Responses strings or Claude success content whose parsed root contains `error` fail with `tool_result_error_semantics_conflict`. Nested business `error` keys remain ordinary data. Root error interpretation follows the declared native envelope, never arbitrary keys in arguments, schemas, output payloads or text. `MaxToolErrorBytes` bounds the raw UTF-8 error text and, for the object format, the complete compact JSON representation; invalid Unicode and over-limit data fail without replacement/truncation. The limit applies only to explicitly converted errors and is not a provider quota.

Original call IDs and names still undergo the existing pending-call tracker before result conversion. Parallel same-name calls, reverse result order, duplicates, orphan IDs and ambiguous missing IDs retain their established constraints. No tool is run, retried, repaired or reordered. Request-history errors do not change upstream model-error/HTTP/SSE handling or permit a successful terminal after a late stream error.

The selected tool-error format and byte limit are included in configuration fingerprints. A changed configuration requires a new Session ID or caller-controlled expiration; native provider bindings remain independent. Primary contracts: [Gemini FunctionResponse native fields](https://github.com/googleapis/python-genai/blob/main/google/genai/types.py), [Claude tool-result content/error fields](https://github.com/anthropics/anthropic-sdk-python/blob/main/src/anthropic/types/tool_result_block_param.py). Independent goldens and substitute HTTP/SSE tests verify protocol behavior, not actual tool execution or live model acceptance.

Tests include four independently authored complete native request/roundtrip goldens, public SDK complete/SSE final-wire checks, late native errors, default/format/limit failures, exact JSON kinds and Unicode, parallel reverse-order association, metadata diagnostics and real v23 option-shell Session rejection. Test counts describe exercised behavior, not model quality or full protocol parity.

## Explicit generation settings

`reasoning_effort`, Responses `reasoning.effort`, Claude `output_config.effort` and Gemini `thinkingConfig.thinkingLevel` map qualitative intent. Supported levels remain model-dependent; the SDK does not infer capabilities from model names or promise equal token use/quality between suppliers. Claude uses adaptive thinking for an effort-only request. `TargetThinkingBudgetTokens` explicitly selects manual Claude thinking instead; it must be at least 1024 and below the caller's output limit. Output limits are never raised automatically. Manual mode is only usable on models that support it.

Exact numeric budgets transfer between Claude and Gemini; zero/off and dynamic (-1/adaptive) remain distinct. An OpenAI target cannot represent a numeric thinking budget. `TargetReasoningEffort` can explicitly replace it, but produces `reasoning_budget_replaced` and still requires `AllowLoss = true`. Supplying both budget and effort can also be unrepresentable in a destination. Explicit `false` and omitted/null settings are not treated as interchangeable.

JSON Schema bodies are copied unchanged into OpenAI `response_format`/`text.format`, Claude `output_config.format`, or Gemini's selected text-format envelope. The default `GeminiOutputFormat.LegacyJsonSchema` retains `responseMimeType` + `responseJsonSchema`; explicitly choose `GeminiOutputFormat.ResponseFormat` for `generationConfig.responseFormat.text` with `APPLICATION_JSON` / `TEXT_PLAIN` and optional `schema`. Both source envelopes are recognized without model-name heuristics. Native Gemini calls and same-protocol copies remain unchanged, including future fields.

Modern and legacy text envelopes cannot be combined: the converter rejects ambiguous text settings even with `AllowLoss`. Unsupported audio/image format settings and unknown fields inside the modern envelope retain precise diagnostics; an omitted text format does not invent JSON/plain intent. A schema requires the JSON MIME and an object root in this common subset. Current Google references mark the legacy typed `responseSchema` field deprecated; its source dialect is explicitly translated rather than treated as JSON Schema. Support/selection still depends on the configured upstream.

Typed `responseSchema` source support includes required native types (names or official numeric enums), nested `properties`/`items`/`anyOf`, `required`, string enums, nullable types, string/array/object limits, finite numeric bounds and descriptive fields. ProtoJSON camel/snake field names and quoted/numeric int64 exponent forms are accepted; int64 limits are parsed exactly without floating-point rounding. Ordinary ProtoJSON null fields remain unset, while `default: null` and `example: null` remain explicit values (`example` becomes a one-element JSON Schema `examples`). Nullable adds null to the explicit type and does not modify other constraints, including enums. Empty native repeated enum/anyOf fields mean unset, not an invented empty constraint. Conflicting field aliases fail.

For a complete source output-schema `propertyOrdering`, OpenAI targets receive `properties` in that order; Claude or partial ordering produces `schema_property_ordering_not_representable` and requires explicit loss consent. Duplicate/unknown ordering names fail. Unknown typed source validation keywords are hard failures even with `AllowLoss`, including nested/null values; JSON Schema-only keywords belong in a JSON Schema source envelope instead. No constraints are deleted to fit the old dialect. Offline wire tests do not prove that a model accepts every Schema keyword or combination.

Explicitly select `GeminiOutputFormat.LegacySchema` to write `responseMimeType` + typed `responseSchema` from an OpenAI or Claude JSON Schema request. The default remains `LegacyJsonSchema`; same-protocol copies do not migrate envelopes. Typed targets accept explicit single types or a type-plus-null union, nested properties/items/anyOf, string enums, required/string/array/object limits and representable numeric bounds. Int64 counts become decimal strings. Numeric bounds must remain the same decimal value when serialized as the upstream double; unrepresentable precision, overflow and underflow fail rather than rounding the constraint. Strict OpenAI text-schema property ordering propagates recursively; Claude and non-strict OpenAI schemas do not gain an invented order guarantee.

`$ref`, `$defs`, closed/schema-constrained `additionalProperties`, const, non-null multi-type unions and other unavailable validation keywords hard-fail even with `AllowLoss`. Explicit `additionalProperties: true` is equivalent to the native open object and needs no target keyword. Empty/non-string/null-containing enums cannot become an unrestricted native enum. One `examples` entry becomes `example`, including null; multiple examples require `schema_examples_not_representable` consent and retain only the first target annotation while the complete source remains in `OriginalData`. This annotation loss never authorizes dropping validation constraints. Native/default JSON Schema paths retain their original constraints.

Gemini function definitions accept typed `parameters` input through the same reviewed dialect mapper; `parameters` and `parametersJsonSchema` together fail even with `AllowLoss`. Ordinary function parameters do not inherit a text-format output-order guarantee: nonempty typed `propertyOrdering` is diagnosed for every cross-protocol target. Unknown typed constraints still hard-fail before HTTP. Complete request snapshots and public SDK tests preserve the function schema, choice, call IDs, argument JSON and associated result history; replay back to Gemini uses unchanged JSON Schema under `parametersJsonSchema`. Native Gemini copies preserve the original typed definition.

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

Summary/display intent maps to Responses `reasoning.summary`, Claude thinking `display`, and Gemini `includeThoughts` where possible. Request-setting support does not authorize conversion of signed thinking or encrypted reasoning returned by the model; those responses still require native APIs. Unsupported summary detail levels and unknown nested settings remain diagnosed. All options and the conversion algorithm version participate in Session binding identity; changed options invalidate old bindings. The converter uses `conversion-v25`; Sessions from the previous conversion algorithm are rejected rather than silently rebound. Native-provider Session identities are not changed by this conversion-package version.

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

History replay preserves custom/function result kinds and consumes each ID once. Completed source item ID/status/`created_by` stay in `OriginalData`; foreign item IDs do not become another supplier's item identity. New local response item IDs and completed/incomplete status remain present. Custom strings retain exact text in Chat/Claude, while Gemini receives the explicitly opted-in object `{"output": text}`. Supported custom result arrays use the separate rich-history mapping above; Gemini arrays and other unrepresented kinds remain explicit boundaries.

Only omitted `format` or `{"type":"text"}` is supported. Namespaces require the independent option below. Grammar/CFG, program callers, async/deferred execution, mixed hosted tools and unknown custom fields hard-fail even with `AllowLoss`. Native calls and same-protocol copies preserve all original structures.

Defaults bound the combined custom/ordinary name map to 128 entries, historical calls to 4096, returned calls to 128 and each decoded custom input/string result to 1 MiB of strict UTF-8; a custom result array is bounded by its full source JSON UTF-8 size. Positive `MaxToolConversionMappings`, `MaxToolConversionHistoryCalls`, `MaxToolConversionOutputCalls` and `MaxCustomToolInputBytes` override these limits and enter binding identity. With an active custom context, each source-stream argument buffer is limited to `6 * MaxCustomToolInputBytes + 64` UTF-8 bytes, including ordinary functions in that stream; this allows JSON escaping and wrapper overhead without accepting unbounded padding. Counts are checked before aggregation, not just at EOF. Oversized/invalid text fails without truncation. Unmapped native/stateless streams keep their existing behavior. Typed Gemini function targets still reject unrepresentable closed-object constraints; use the default JSON Schema function target for this wrapper. Independent complete/stream goldens, public SDK replay/concurrency and rejection tests run offline; no live custom-tool verification was performed. See [OpenAI custom tools](https://developers.openai.com/api/docs/guides/function-calling#custom-tools) and [custom input events](https://developers.openai.com/api/reference/resources/responses/streaming-events#response.custom_tool_call_input.delta) for native contracts.

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

`HostedSearchTarget` declares the native search structure of the fixed converting target. `None` disables cross-protocol search by default, including when ordinary loss is allowed. Supported declarations are `ResponsesWebSearch` (`web_search`), `ClaudeWebSearch20250305` (classic direct search), `GeminiGoogleSearch` (`googleSearch`) and `ChatWebSearch` (`web_search_options`, with verified search-before-answer behavior). The actual target protocol must match. No model-name lookup, automatic version upgrade, SDK query execution, separate search service or retry is added. A target declaration alone never enables search for an ordinary request.

```csharp
var options = new LlmConversionOptions
{
    HostedSearchTarget = LlmHostedSearchTarget.ClaudeWebSearch20250305,
    DefaultMaxTokens = 512
};
// Explicit declaration of your configured target, not certification of model/account access.
// Set AllowHostedSearchWithFunctionTools only after verifying that target's combination support.
```

This implementation implements **request configuration and output boundaries**, not full hosted-search conversation conversion. The six directions between Responses, classic Claude and Gemini have independent complete request goldens and public HTTP/SSE tests. Their substitute replies contain ordinary text; these tests do not prove that a model searches, accepts a setting, or returns correctly mapped sources/citations. Native APIs already carry complete provider tools and results. Use native Claude for conversations requiring encrypted search result replay.

One search declaration is supported per request. Responses GA `web_search` and its documented dated alias, Claude classic `web_search_20250305` with default/direct callers, Gemini `googleSearch` with default or explicit text-only `webSearch`, and explicit Chat `web_search_options` are recognized. Native/same-protocol calls preserve all versions and unknown fields. Preview tool versions, Claude dynamic code filtering, image search, legacy Google retrieval and additional server tools remain native-only in this slice.

Bare DNS allow-domain lists and explicit approximate city/region/country/timezone transfer between Responses and classic Claude without normalization or inferred locations. Unknown settings, null/empty restrictive lists, paths/wildcards/IP filters, unsupported domain/time restrictions, offline-only access, search call limits (including zero/null presence) and unverified parallel controls hard-fail even with `AllowLoss`. Google Search has no corresponding fields for these source constraints in this mapping. Explicit live access permission uses the peer's native live-search availability; this is not a freshness guarantee. Missing location/settings retain provider defaults, not a fabricated common default.

`search_context_size` has no equivalent Claude/Gemini field. Explicit `AllowLoss = true` can omit it with `search_context_size_not_representable` and the complete original request retained; the SDK never turns it into token or search-count ratios. Other search access/scope/count constraints are not ordinary annotation losses. Generic `TargetSettings` checks still run on the final native request.

Responses and classic Claude global auto/required/none and specific native search choices map where represented. Definitions are retained for `none`. Gemini function-calling modes cannot force or disable Google Search: unsupported trigger choices fail rather than using `functionCallingConfig` or deleting tools. Only omitted/auto intent maps to Gemini in this slice. Function mixing requires the separate `AllowHostedSearchWithFunctionTools` declaration, and native name collisions still fail. Namespace/custom options remain independent; hosted tools never enter their function identity table.

Known unrepresented search execution, errors, citations and generateContent `groundingMetadata` receive specific diagnostics. Complete OpenAI URL citations have the separate mapping below. Default strict mode rejects unrepresented business output. Ordinary loss consent retains complete source JSON for complete conversions, while stream diagnostics accumulate under the existing per-event metadata contract; it does not make these results lossless or guarantee that every unsupported search transcript can complete. **Claude search result references, encrypted search content and encrypted citation indices remain hard native-only boundaries**, even with loss consent. This guard checks native content positions, not arbitrary function arguments or user JSON. Broader results/grounding/citation/offset mappings and additional trigger semantics remain pending.

### Chat search configuration

Chat search models search before answering; this is different from optional native search tools. Chat → Responses/Claude therefore emits a genuine search definition **and a named required search selector**. Reverse conversion accepts a named search selector, or `required`/Claude `any` when search is the only tool. It emits `web_search_options` without inventing a function or Chat search selector. Omitted/auto/none/function choice cannot become Chat search, even with `AllowLoss`. Chat ↔ Gemini stays a hard trigger boundary because Google Search has no verified force-search selector in this mapping. The SDK never rewrites model names; caller-configured endpoints/models must support the declared operation. This preserves required-search intent, not search algorithm, number of subqueries, ranking or answer equivalence.

Chat `user_location` wraps its fields inside `{"type":"approximate","approximate":{...}}`; Responses/Claude use flat fields. Country/city/region/timezone transfer unchanged, including casing. `search_context_size` transfers unchanged between Chat and Responses. Chat → Claude can omit that guidance only with its existing specific loss diagnostic and explicit consent. Missing settings gain no common default. Chat domain filters, offline access and call limits have no equivalent here and remain hard boundaries.

Chat/function combinations are not verified by this contract. They fail even when the existing `AllowHostedSearchWithFunctionTools` switch is true; that switch still applies only to the original three-protocol combinations. Chat-side explicit `tool_choice`, parallel controls, malformed locations, null settings and unknown fields are not silently removed. Native copies remain unchanged. The new enum and `conversion-v21` algorithm enter Session binding.

Tests cover four independent full request comparisons and reverse replay, tests, exact locations/context, declaration binding, native copies and semantic failures before HTTP. Substitute replies contain plain text; these tests verify request configuration and normal terminal handling, not live search execution, result/citation conversion or model acceptance. Existing separate result/citation limits continue to apply.

Contracts checked: [OpenAI web search and Chat search behavior](https://developers.openai.com/api/docs/guides/tools-web-search), [Chat request fields](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create), [Claude search tools](https://platform.claude.com/docs/en/agents-and-tools/tool-use/web-search-tool), and [Gemini generateContent GoogleSearch](https://ai.google.dev/api/generate-content#GoogleSearch). No live hosted search verification was performed.

## Complete OpenAI URL citations

Complete Chat `message.annotations[].url_citation` and Responses `output_text.annotations[]` map in both directions. URL/title strings, array order, repeated sources and integer ranges are retained; source JSON remains in `OriginalData` or `Metadata.Conversion.UpstreamData`. Text is unchanged. Function call IDs/arguments remain independent, and a search execution record is never synthesized as a client function. Other annotation kinds and unknown citation fields are diagnosed individually.

When Responses text blocks are concatenated into Chat text, citations with an empty or entirely ASCII preceding text can be shifted by that prefix length. A preceding non-ASCII character produces `citation_offset_unit_unknown`; strict mode fails, while explicit loss consent omits only the affected citations and keeps their original data. The references describe character indices without specifying UTF-16, Unicode scalar or UTF-8 units. For unchanged text, indices are copied without reinterpretation. Validation rejects missing/noninteger/negative/reversed indices and ranges beyond the UTF-8 byte-length upper bound; that plausibility bound does **not** certify exact Unicode boundaries. The SDK never uses `.Length` to guess a Unicode offset.

This is **complete-response support only**. The verified [Chat chunk contract](https://developers.openai.com/api/reference/typescript/resources/chat/subresources/completions#chat-completion-chunk) has no citation delta field, and Chat assistant request parameters do not establish an annotation replay field. Converted history and Responses citation streams therefore retain their explicit strict failure/consented-loss boundaries; no invented Chat event or request field is emitted. Native Responses streams preserve `response.output_text.annotation.added` and complete snapshots unchanged. Gemini complete Web grounding is covered below; Claude citations and full hosted-search conversation conversion remain separate work. See the [Responses annotation event contract](https://developers.openai.com/api/reference/typescript/resources/responses#response-output-text-annotation-added-event).

Tests cover two authored full response goldens, ASCII rebasing, Unicode boundaries, invalid/unknown fields, source immutability, function association, tests and native/strict/consented stream behavior. No live search was performed.

## Complete Gemini Web grounding and declared index units

`OpenAiCitationIndexUnit` declares the **verified behavior of the configured OpenAI endpoint**, rather than a model lookup or a request to change the endpoint's convention. Its default `Unspecified` retains the OpenAI behavior above. An explicit `UnicodeScalar`, `Utf16CodeUnit` or `Utf8Byte` declaration enables exact boundary validation and Unicode-prefix rebasing. Combining marks remain separate scalars; no normalization or grapheme counting occurs. UTF-16/UTF-8 ranges cannot split an encoded scalar. The option is snapshotted into the conversion context and Session binding (`conversion-v23`); changing it cannot reuse the old binding. Native same-protocol copies remain unchanged.

With that declaration, **complete Gemini → Chat/Responses** maps `groundingMetadata.groundingChunks[].web` and `groundingSupports[]` to URL annotations. Google defines segment ranges in bytes; part indices address the original native parts, including intervening functions. Proto3 omitted non-optional indices default to zero. The mapper validates UTF-8 boundaries, optional segment text, original part/source indices and HTTP(S) Web URIs, retaining exact URL/title strings, source order and duplicate references. Chat ranges are rebased across concatenated text using the declared endpoint unit; Responses ranges stay local to each output text block. Broken association/ranges remain hard failures even with `AllowLoss`.

Missing Web URI/title, retrieved-context/Maps sources, uncited source entries and supports without sources have explicit diagnostics. Queries, search-entry HTML, confidence scores and unknown metadata, including null/false fields, also require ordinary diagnosed loss consent. Representable annotations survive that consent and original JSON remains available separately. The SDK does not turn query strings into search execution IDs, convert private sources to public URLs, fetch cited pages or execute search. Gemini `citationMetadata` code/license citations are a different contract and remain unmapped. Empty metadata is valid; malformed null/array metadata is not treated as empty.

The index-unit declaration alone does **not** enable grounding stream conversion or citation history replay. Only the separate explicit Responses ↔ Gemini single-text-part mode maps these streams; other modes keep strict failure/consented omission; native Gemini streams retain their full frames. Claude citations remain separate work. Signed thoughts and encrypted replay state stay native-only even with consent. No live search/model acceptance was verified. Contracts: [Google segments and grounding](https://ai.google.dev/api/generate-content#Segment), [official API discovery schema](https://generativelanguage.googleapis.com/$discovery/rest?version=v1beta) (revision `20261017`), and [OpenAI citation fields](https://developers.openai.com/api/docs/guides/tools-web-search#output-and-citations-1).

With an explicit unit declaration, **complete Chat/Responses → Gemini** also maps URL annotations to Web grounding. Each annotation becomes one chunk and one support in its original order, retaining repeated URL/title pairs and overlapping or empty ranges. Segment offsets are converted to UTF-8 bytes within the actual emitted text part; intervening function parts count toward `partIndex`. `segment.text` is the exact source substring, without Unicode normalization. Text and function IDs/argument objects remain unchanged. No search queries, confidence values or execution IDs are invented.

Only absolute HTTP(S) sources can become Web chunks. Other URI forms require explicit `AllowLoss`, report `grounding_source_not_representable`, and omit only affected citations. Unknown annotation fields retain their own diagnostics. Malformed ranges and split Unicode scalars hard-fail even with loss consent. With `Unspecified`, the previous `search_citations_not_representable` boundary remains, including ASCII text; no endpoint unit is inferred. Responses with no representable citations receive no empty grounding metadata. Original JSON remains independent in `OriginalData` or `Metadata.Conversion.UpstreamData`.

The separate explicit single-text-part Responses → Gemini stream profile maps validated URL citations; Chat streams and other layouts remain explicit boundaries. Complete reverse mapping alone never enables stream mapping. Native copies remain unchanged. This is protocol validation with substitute HTTP, without live search or model-quality claims.

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

The decorator implements optional `ILlmCredentialSupport` by delegating to the fixed upstream. A legacy upstream remains API-Key-only; an explicitly anonymous-capable custom upstream can be used without a fake Key. The choice does not change protocol conversion, add native Ollama support or bypass complete Session binding. Tests verify complete/stream public conversion, support declarations, eager legacy rejection and cached authentication-kind changes. Existing API Key fingerprints and `conversion-v23` algorithm/options remain unchanged.

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

The LLM core public snapshot guard rejects malformed original DOM strings/property names and extension string escapes before conversion or cache access. Eight direct typed Claude/Gemini complete/stream calls first reproduced changed error text and collapsed distinct malformed tool IDs; they fail before HTTP. Tests deliberately avoid preliminary deserialization of malformed values. That snapshot-encoding contract left conversion-v24 unchanged; the current conversion algorithm identity is conversion-v25. See the [core encoding contract](../Asgard.Loki.LLM/README.md#native-json-snapshot-encoding).

### Explicit settings through a fixed native Messages transport

Sixteen additional public SDK regressions and an independently authored complete source/native request/result/SSE fixture exercise Chat reasoning `high`/`none` and strict JSON Schema through the Anthropic native transport, with explicit Key/Bearer header selection. High effort writes explicit native effort/adaptive thinking; none writes disabled thinking. No budget is calculated and the 4096 output limit stays unchanged. Schema constraints and the exact large-integer minimum are preserved in the same `output_config` object as effort. The OpenAI schema name has no Claude equivalent: positive tests require explicit `AllowLoss` and the specific `format_metadata_not_representable` diagnostic; strict mode fails before HTTP. Explicit manual budgets cannot override a source off mode.

Late native source errors remain original HTTP failures and cannot produce converted success. Complete native output stays in conversion metadata. Legal full streams align indices with final `content` array positions, as specified by the [Claude streaming contract](https://platform.claude.com/docs/en/build-with-claude/streaming). Tests retain the existing strict boundary for a single-block stream that starts at 7 or int.MaxValue. Native passthrough can preserve such raw indices without thereby certifying a valid complete Claude array. No conversion algorithm, provider, public interface or production dependency changes in this implementation; conversion-v25 remains current. These are offline settings/transport tests, not endpoint/model acceptance of Bearer, reasoning or schemas.

The new native `LlmProtocol.BedrockNova` flag does not extend this four-protocol conversion matrix. Tests reject Nova requests, complete responses, streams and same-protocol copies in both directions across all supported peers. Use the native SDK entry for Nova JSON; no Nova-to-Chat schema, reasoning, tool or stream mapping is implied. Conversion algorithm identity remains v25.

### Typed source stream errors

Claude Messages and Responses use their own event discriminator for cross-protocol stream errors: `error` fails for both; `response.failed` fails for Responses. A Claude event named `response.failed` is an unknown extension, not an imported Responses failure. Untyped root error envelopes remain failures; the existing Chat/Gemini error guards remain unchanged. This matches the distinction between [Claude errors and unknown event types](https://platform.claude.com/docs/en/build-with-claude/streaming#other-events) and [Responses native streaming events](https://developers.openai.com/api/reference/resources/responses/streaming-events).

A non-error typed event containing a root `error` extension still goes through field diagnostics. Default strict conversion rejects unrepresented fields/events. With explicit `AllowLoss`, the known subset may convert while retaining `unsupported_field`/`unknown_event` diagnostics; the converter does not guess that an error-shaped business object represents an upstream failure. Nested function arguments are opaque JSON data and are never scanned as protocol errors. Actual error types cannot be bypassed with loss consent.

When an event produces target deltas, their `OriginalData` (or public conversion metadata) retains the whole independently copied source event. Metadata-only unknown events may produce no target delta; diagnostics accumulate, but the converter does not promise to attach every skipped source frame to a later result. Use the native stream when every original event is required. Same-protocol copies remain complete copies.

`typed-stream-errors.json` independently authors full Claude/Responses source streams and one complete Chat target sequence, including split original tool arguments, error-shaped business data, null/false and exact 9007199254740993. Fifty-six offline regressions cover strict/consented extensions, source immutability, exact tool IDs/parameters/usage, mismatched association, invalid argument snapshots, missing terminals and public SDK/fragmented-SSE read/dispose/cancel/late-error failures. No target success is emitted before normal source EOF/disposal. Request/result mappings, conversion-v25 binding identity and supplier/package boundaries are unchanged. These checks are not live model acceptance or new Nova/Converse conversion support.

### Explicit Nova ↔ Converse complete JSON

`LlmProtocolConverter.ConvertRequest` / `ConvertResponse` additionally support `BedrockNova` ↔ `BedrockConverse`. This is a separate native pair: conversions between either Bedrock protocol and Chat/Responses/Claude/Gemini remain unsupported, even with `AllowLoss`. Ollama stays outside both matrices. Same-protocol request/response/event copies preserve complete JSON without requiring a cross-protocol format declaration.

| Field or operation | Supported mapping / boundary |
| --- | --- |
| Complete requests | Ordered user/assistant messages and system text; Base64 images and Base64/caller-owned S3 video containers; ordinary tool definitions, selection, calls and text/JSON results |
| Association / Schema | Exact original IDs, parallel same-name calls and reverse-order result replay; explicit status versus absence; unchanged tool JSON Schema, null/false and exact integers |
| Common generation | Explicit maxTokens, temperature, topP and stopSequences; omission stays omitted |
| Nova settings | Explicit topK and reasoningConfig enabled/disabled plus low/medium/high; selected Converse additionalModelRequestFields layout; no effort/budget heuristic or sampling/token adjustment |
| Complete responses | Ordered text/ordinary tool output, original stopReason, required native input/output/total counters and latencyMs; no ID/stop/count synthesis |
| Native-only boundaries | Document input, reasoning/signatures, cache/guard/citation/audio/search blocks, outputConfig, guardrailConfig, promptVariables and strict tool constraints reject even with loss consent |
| Unknown extensions | Strict rejection by default; explicit loss consent returns source JSON and exact diagnostics while copying only the proved subset |
| Streams | Dedicated native pair logical text/tool streams via ConvertingBedrockLlmProvider; stop/usage withheld until source success. Identity streams retain original events/frames; mixed four-text-protocol pairs remain unsupported |

Every cross-protocol request requires `BedrockNovaRequestFormat.MessagesV1` (write/require `schemaVersion: messages-v1`) or `Nova2` (omit/require absence of that field). This chooses a wire envelope, not model support or entitlement. When topK/reasoningConfig appears, also choose `BedrockConverseInferenceFormat.Direct` or `NestedInferenceConfig`. [Nova v1 Converse](https://docs.aws.amazon.com/nova/latest/userguide/using-converse-api.html), [Nova 2 schema](https://docs.aws.amazon.com/nova/latest/nova2-userguide/request-response-schema.html) and [Nova 2 core inference](https://docs.aws.amazon.com/nova/latest/nova2-userguide/core-inference.html) show differing wrapper placements. Loki neither guesses which a selected endpoint accepts nor merges/migrates conflicting locations. Wrong/duplicate locations fail even with loss consent. Native clients remain the unfiltered route for supplier-specific payloads.

```csharp
var options = new LlmConversionOptions
{
    BedrockNovaRequestFormat = BedrockNovaRequestFormat.MessagesV1,
    BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.NestedInferenceConfig
};
var converted = new ConvertingBedrockLlmProvider(
    native, LlmProtocol.BedrockNova, "explicit-nova", options);
// native is an application-supplied IBedrockNovaAdapter, e.g. AwsLlmProvider.
// Register converted in LlmOptions.Providers and select its Id on a complete target.
// A public Converse target declares Protocols = LlmProtocol.BedrockConverse;
// client.LLM.BedrockConverse.ConverseAsync(...) then explicitly invokes Nova.
// Preserve the ordinary caller-owned SessionStore, coordinator and credential setup.
```

The fixed upstream must implement the selected native adapter. `ConvertingBedrockLlmProvider` declares only the two Bedrock interfaces; existing `ConvertingLlmProvider` retains only its four original protocol interfaces. Neither decorator can wrap another converter. The immutable native request context records original historical tool IDs; a new response reusing one fails instead of being renamed or ambiguously associated. Direct response-only conversion cannot assert request-history uniqueness without that context. The new `bedrock-json-conversion-v1` identity includes fixed upstream/credential support, native format and layout; changed format/layout rejects old Sessions before acquisition. Default-omitted new enum options preserve exact existing conversion-v25 option bytes. AWS/core native identities and production package references are unchanged: Conversion depends only on LLM, AWS only on LLM plus HTTP dependencies.

Unknown fields never grant permission to drop signed state, tool associations or execution/format guarantees. Native reasoning blocks remain native-only even though request reasoning settings map. Text outputConfig and strict tool enforcement are distinct from copying a tool's JSON Schema. The four-protocol `TargetSettings` contract is rejected for this native pair rather than silently ignored. Malformed required counts, role/union/ID/result errors and high-effort sampling conflicts fail without rewriting the caller's request. Complete results remain contingent on the upstream adapter's normal EOF/disposal/cancellation contract. No network media resolution, tool execution, retries or nonstream fallback is added.

The shared independent test artifact has six full native request goldens and four response goldens. Tests replace tests, and tests exercise declaration boundaries, sixteen complete public HTTP/auth/body paths, no-HTTP failures, Session policy changes, native JSON faults and original binary attachments. No live model/account acceptance, universal wrapper-layout support, Linux or remote CI result is claimed. This was complete JSON support; stream support below adds the common logical text/tool subset. Broader native stream content and Bedrock ↔ four-protocol mappings remain subsequent work.

### Native pair logical stream data API

`BedrockProtocolStreamConverter` is the explicit data-level Nova ↔ Converse stream API. Construct it with the two native protocols, or with a previous request's immutable `LlmConversionContext` to reject reused historical tool IDs. `Push(JsonObject, sourceEventName)` accepts a whole Nova inner envelope or a Converse payload plus its required separate source event name. `LlmProtocolStreamConverter` remains the original four-protocol API (and native identity-copy helper); its Bedrock cross-stream rejection is retained so it never guesses a missing transport discriminator.

`BedrockStreamConversionResult` carries target `EventType`, complete target `Data`, untouched source `OriginalData`, raw `SourceEventName`, diagnostics and optional context. EventType is a logical target type, not an original binary header. No CRC/header/Base64 frame is generated or attached. Native identity copies stay complete copies. The data API introduces no HTTP operation; the public SDK composition below owns enumeration of the selected native adapter separately.

Only the proved common message/text/ordinary-tool/stop/usage/metrics lifecycle maps. Original sparse int32 block indices, IDs, argument fragments, source order and counters remain unchanged; interleaved same-name calls have separate argument buffers. Tool closure requires an unchanged complete JSON object and rejects duplicate properties, invalid Unicode and incomplete arguments. Signed/reasoning, hosted/result/media/citation/cache/guard blocks remain hard native-only boundaries even with loss consent. Metrics and all required counters must be present; no latency/count/stop is invented. Unknown extensions require explicit loss consent and preserve precise envelope-versus-payload paths. Unknown metadata-only events may produce no target result; use native streams to retain every original frame.

MessageStop and metadata stay held until `Complete()`, which the caller must invoke only after normal source enumeration, asynchronous disposal and final cancellation checks succeed. Missing/duplicate/reopened state or any later strict loss/error prevents completion. No successful terminal is synthesized from EOF. Every converter is single-use after success or failure.

`MaxBedrockStreamBlocks` defaults effectively to 128 across the entire converted stream; sparse indices use dictionaries. `MaxBedrockStreamToolArgumentBytes` is 1 MiB per active ordinary tool's original argument text encoded as UTF-8. `MaxBedrockStreamTailBytes` is 1 MiB for held source-plus-target JSON serialized to UTF-8; `MaxBedrockStreamDiagnostics` is 1024 cumulative records. Positive optional values are SDK resource bounds, not model quotas. Overflows fail instead of trimming state or diagnostics. Default null omission preserves exact preceding option bytes; explicit policies enter ordinary decorator bindings. These limits do not filter native identity streams.

Two independently authored complete 15-event native sequences cover empty/Unicode text, sparse maximum indices, interleaved/reverse-close tools and exact business-error arguments. Tests verify full target snapshots, original events, strict/consented fields, errors, source integrity, opaque boundaries, resource limits and prior option compatibility. The SDK and actual native transport integration are verified separately; these tests do not contact models or certify live account/media/tool behavior.

### Public SDK native stream composition

The same `ConvertingBedrockLlmProvider` implements the common Nova ↔ Converse **logical event** stream subset. Keep the explicit complete-target/model/credential, Nova request-format and Converse setting-layout configuration shown above. Public `BedrockConverse.StreamAsync` fixed to Nova invokes native InvokeModelWithResponseStream; public `BedrockNova.StreamAsync` fixed to Converse invokes native ConverseStream. It never adds a stream body switch, falls back to a complete call, retries or executes tools.

```csharp
await foreach (var item in client.LLM.BedrockConverse.StreamAsync(
    converseRequest, new LlmCallOptions { SessionId = "native-pair" }, cancellationToken))
{
    // item.Type is the target logical event name; item.Data is a Converse payload.
    // Observe the entire enumeration: a late source error still fails the call.
}
```

Converted Converse events expose the target logical EventName/Type; converted Nova events expose a whole inner JSON envelope and explicit Type while EventName stays null. Neither has an original AWS frame attachment. `Metadata.Conversion.UpstreamData` retains complete source JSON and cumulative request/stream diagnostics; it does not promise binary headers or every skipped unknown source event. The direct data result separately exposes SourceEventName. Same-protocol native identity streams return the exact original event instances and retain supplier frames, native outer names and opaque fields.

The converter holds messageStop/metadata until native enumeration, asynchronous iterator disposal and the final cancellation check all succeed. Read/CRC/length/truncation/native errors, strict losses, malformed associations, invalid arguments or disposal faults cannot first publish converted success. Text/tool fragments remain incremental. Early exit disposes only the active source. Cancellation between final logical events suppresses subsequent delivery; the source is already finished/disposed when the first terminal is delivered. Required usage/metrics remain original: a Nova usage-only tail cannot become a valid Converse metadata event by inventing latency.

Nullable stream policies preserve the exact preceding `bedrock-json-conversion-v1` default binding bytes; its historical domain name remains for complete-JSON compatibility. Explicit policy changes enter the full target fingerprint and reject existing Sessions before signing/HTTP. Native AWS identities, four-protocol conversion-v25 and the production package graph stay unchanged. This is response-data conversion, not binary frame translation or an application task engine.

Tests cover complete independent request/event goldens, sixteen native HTTP/auth paths, real source framing/errors, state and policy limits, prior bindings and source disposal. All credentials/transports are substitutes; no real cloud/model/layout acceptance, Linux run or GitHub Actions result is claimed. Broader reasoning/media/result/citation streams and mixed four-text-protocol mappings remain unfinished.

### Declared conversion protocol scope

Both conversion decorators implement the optional core `ILlmProtocolSupport` contract. `ConvertingLlmProvider` currently declares its four original protocols; `ConvertingBedrockLlmProvider` declares Nova/Converse only. None, combined values and unrelated native protocols return false. Each constructor also respects the fixed upstream's own optional protocol declaration instead of relying only on a physically present interface. Legacy upstreams continue to use their existing interface checks.

This foundation allows future partial native/text conversion combinations to be rejected at SDK registration until actually implemented. It adds no conversions today and does not infer model capabilities, execute requests, change exact existing conversion identities or introduce provider dependencies. Five offline regressions verify the declared matrix and pre-call rejection of an upstream that physically implements but explicitly disallows the selected native protocol.

### Four text requests to native Converse: complete data conversion

`LlmProtocolConverter.ConvertRequest(request, publicProtocol, LlmProtocol.BedrockConverse)` accepts the common complete-call subset of Chat, Responses, Claude and Gemini. Convert the reply with the returned immutable context. Ordinary function IDs and reverse-order results remain associated by ID. Gemini object results become native JSON, while JSON-looking text results remain text. Tool and output JSON Schema keep all constraints and exact numeric values. Native `outputConfig.textFormat.structure.jsonSchema.schema` is a JSON string; optional names are never invented ([AWS contract](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_JsonSchemaDefinition.html)).

Converse replies have no model/response ID fields. The context supplies the requested model label; `loki_` IDs are synthetic, non-resumable SDK labels. Without context, the model label is empty. Required original usage/metrics must exist; no counts or latency are invented. Unknown ordinary extensions require explicit loss consent and remain in OriginalData. Tool role/ID errors, hosted/custom tools, continuation, model-specific reasoning/topK, parallel guarantees, tool choice none, opaque content, nonzero cache accounting, mixed streams and the inverse request/response directions remain explicit boundaries. Target sampling ranges are checked without scaling. Native calls remain available for their full original payloads.

### Public SDK text calls over native Converse

The existing `ConvertingBedrockLlmProvider` implements the four text adapters. Fix its upstream to **BedrockConverse**, register the desired text protocol flags on the existing complete route target, and call the normal Chat/Responses/Claude/Gemini complete client. Initialization and credential/session handling remain the native supplier/core responsibilities:

```csharp
// native is an application-created AwsLlmProvider with its existing HTTP/auth configuration.
var converted = new ConvertingBedrockLlmProvider(
    native, LlmProtocol.BedrockConverse, "converse-text");
// Register converted in LlmOptions.Providers and use converted.Id on the route target.
// Set target.Protocols explicitly, e.g. ChatCompletions | Responses, plus its existing
// UpstreamModel/CredentialId/server settings. Use the ordinary public SDK complete call.
```

Fixed Converse supports the four complete text entries plus the existing native pair. Fixed Nova declares only the native pair; text targets fail during configuration even though the class implements those interfaces. Mixed text streams throw before native credential acquisition/HTTP, without complete-call fallback. Neither decorator may wrap the other. Default binding serialization remains unchanged; protocol flags already belong to complete target/session identity. The supplier production package still has no Conversion dependency.

The complete golden response model label is the routed actual request model, not a claim that Converse echoed a model ID. `Metadata.Conversion` retains the native response and diagnostics, while normal metadata retains the selected complete target. Native EOF/read/disposal/cancellation rules still guard delivery. Tests cover eight complete native HTTP/auth paths, no-HTTP mixed streams and strict request losses, and read/disposal failures. Four additional constructor regressions prevent newly possible nested decorators. This is offline SDK protocol evidence; no real account/model/network or GitHub Actions was used. Mixed streams, inverse calls and model reasoning dialects remain pending.

Nova2 high reasoning additionally rejects an explicit `maxTokens` in either native conversion direction, including loss-consented calls. The converter does not delete/increase that limit. The positive high test omits it explicitly; positive v2 goldens retain 8192 and use medium. See the [AWS high-mode restriction](https://docs.aws.amazon.com/nova/latest/nova2-userguide/extended-thinking.html). This check follows the explicit Nova2 format policy, not a guessed model suffix; raw supplier passthrough stays raw.

Converted Responses IDs (including the synthetic `loki_` labels from Converse) are output labels, not resumable upstream Responses objects. Core complete/stream clients leave them out of native response-ownership registration while preserving JSON/metadata. A continuation with a new converted ID fails before a second upstream invocation. Native identity results remain eligible for the existing ownership contract, without prefix heuristics or cache coordination changes. Older converted ownership records are not proactively removed; cross-protocol continuation remains rejected by the converter.

### Explicit Converse model reasoning request settings

Set `LlmConversionOptions.BedrockConverseModelDialect` explicitly to `Anthropic` or `AmazonNova2` for four-text requests targeting Converse. `Unspecified` retains the preceding hard boundary. Selecting a dialect declares the caller's intended parameter contract; it does not inspect a model name, query model support or certify that every model in that family accepts each setting.

```csharp
var anthropicPolicy = new LlmConversionOptions
{
    BedrockConverseModelDialect = BedrockConverseModelDialect.Anthropic
};
var converted = new ConvertingBedrockLlmProvider(
    native, LlmProtocol.BedrockConverse, "converse-anthropic", anthropicPolicy);

var novaPolicy = new LlmConversionOptions
{
    BedrockConverseModelDialect = BedrockConverseModelDialect.AmazonNova2,
    BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.Direct
    // Select NestedInferenceConfig instead only for the explicitly intended wrapper contract.
};
```

| Request setting | Anthropic dialect | AmazonNova2 dialect |
| --- | --- | --- |
| Reasoning mode | Explicit manual/adaptive/off; qualitative source effort can select adaptive | Low/medium/high or explicit off |
| Native fields | `additionalModelRequestFields.thinking`, `output_config.effort`, `top_k` | `reasoningConfig` and `topK` in the separately selected direct/nested wrapper |
| Numeric budget | Preserve it; minimum 1024 and strictly below original explicit output limit | Requires explicit `TargetReasoningEffort` low/medium/high plus loss consent and replacement diagnostics |
| Display/summary | Active thinking summarized/omitted; summary-detail changes are diagnosed | Unsupported output intent is diagnosed; no pretend native summary field |
| High/sampling conflicts | Active thinking rejects explicit sampling/forced tools/assistant prefill | High rejects maxTokens, temperature, topP and topK |
| Structured output | Existing native `outputConfig` remains independent and unchanged | Same native schema contract |

`TargetThinkingBudgetTokens` can explicitly select an Anthropic manual budget; incompatible source off/budget values fail. No effort-to-budget ratio, limit adjustment, sampling deletion or model inference is performed. Omitted fields stay omitted, explicit null is not a default, and invalid source modes remain hard failures under loss consent. The default omitted dialect property preserves preceding binding bytes. Explicit dialect/layout/other policy changes enter the existing complete-target identity and reject an old Session before credentials/HTTP.

The initial settings contract supported **request settings** and ordinary text/tool replies only. Current Claude/Converse Anthropic signed-data support is described below. Outside the explicit Claude/Converse Anthropic signed-data path below, native reasoning/signature/redacted response blocks and history remain hard boundaries, including under loss consent. Enabling settings does not discard such blocks to return success. Complete signed-history/response conversion, inverse operations and mixed streams still require separate independent fixtures and implementation; use native Converse to keep its whole protocol. No live thinking, schema enforcement or model/account acceptance is claimed.

Eight independent request goldens plus a plain structured-text native reply/four full public reply goldens describe complete native inputs and results. Tests verify exact schema/number/field placement, strict failure, explicit budget replacement, original snapshots, public SigV4/Bearer HTTP, no-acquisition policy failures and policy-change Session rejection. Public SDK tests deliberately use plain replies and do not simulate verified signatures or real thinking behavior. Source contracts: [Anthropic adaptive settings](https://docs.aws.amazon.com/bedrock/latest/userguide/claude-messages-adaptive-thinking.html), [extended thinking constraints](https://docs.aws.amazon.com/bedrock/latest/userguide/claude-messages-extended-thinking.html), [Nova2 reasoning](https://docs.aws.amazon.com/nova/latest/nova2-userguide/extended-thinking.html).

### Claude / Converse Anthropic signed complete data

With explicit `BedrockConverseModelDialect.Anthropic`, Claude public requests and Converse complete replies preserve native `thinking`/`reasoningText` text plus signature and `redacted_thinking.data`/`redactedContent` Base64. Content blocks remain in their original order around text and ordinary tools. Use the immutable request context to retain the selected model label/history IDs; direct reply conversion keeps the model unknown. Root IDs remain synthetic non-resumable labels.

The same application-selected actual model and complete target/session must be retained when replaying the whole history. [AWS requires the original text, signature and previous messages](https://docs.aws.amazon.com/bedrock/latest/userguide/conversation-inference.html). This codec preserves the strings/bytes without decoding signatures/redacted data, changing Unicode/newlines, renaming tools or combining historical message containers. It does not verify a signature, discover its originating model or make it portable between models/providers/credentials. Pure data conversion has no session store or network; public SDK binding remains the existing core responsibility.

Opaque blocks require exactly the known native branch fields, a Claude assistant role, explicit signature strings and standard Base64 for redacted content. Unknown/missing/null/mixed branch fields fail; no signature or text is fabricated. Missing Converse signatures remain a boundary because a complete Claude thinking block cannot be reconstructed. Signed histories/replies reject **all diagnosed conversion loss even with AllowLoss**, since removing fields can alter signed context. Ordinary unsigned calls retain their preceding loss contract. Business JSON in tool arguments is never scanned as signed protocol data.

Only this Claude/Anthropic complete-data pair is enabled. Chat, Responses, Gemini, Nova2, default/unselected dialects, inverse complete operations and mixed streams retain their prior opaque boundaries. The four-text native decorator does not gain cross-provider opaque conversion. Unknown native extensions remain available through raw native protocols. No live signature, cryptographic validity or cross-model acceptance is claimed.

Four independent complete request/reply/follow-up goldens describe complete native inputs and results. Thirty-one pure regressions cover exact text/signatures/redacted bytes/schema/large integers, mixed same-name tools with reverse results, empty text/zero counters, snapshots, unchanged message boundaries, invalid Unicode/roles/branches and no-loss enforcement. Public SDK tests verify actual HTTP historical replay separately from pure data conversion.

Opaque string/binary fields are checked as protocol values rather than cryptographic proofs: explicit empty signature strings and empty Base64 byte containers remain present and unchanged. Malformed Base64, null and missing required Claude fields still fail. The converter does not invent minimum ciphertext lengths or authenticate a token.

### Public SDK Claude / Converse signed historical replay

The existing Bedrock decorator composes the signed complete-data codec with native Converse HTTP. Keep `BedrockConverseModelDialect.Anthropic`, the same route/actual model/credential policy and SessionId for both calls. Append the first Claude result's **complete content array unchanged** as its assistant history message, retain all preceding messages, and add the application's next user/tool results. Tool execution and history storage stay with the application; the SDK stores only its existing target/session ownership, without a conversation database or background process. Do not use a synthetic root ID as a native continuation handle.

Tests compare sixteen final HTTP bodies across four independent fixtures and SigV4/Bearer. They use actual first-response content in the next request and compare it to a separately authored full native follow-up. Signed/redacted blocks, original message order, same-name tool IDs/reverse results, schema/precision and original native metadata remain intact. Further cases verify pre-acquisition failures, wrong/missing dialect rejection after exactly one HTTP request, late read/disposal/cancel propagation and bound Session policy rejection before another call. No retry/fallback or signed-state sanitization is added. Native identity and binary stream behavior stay unchanged.

These tests use synthetic signature/encrypted values and substitute HTTP/credentials. They prove protocol fidelity and native resource boundaries, not cryptographic validity, live model/schema acceptance or cross-target portability. Other public text protocols/model families and all mixed reasoning streams remain separate work.

### Native provider error boundaries

Conversion preserves failures raised by the selected native provider. In particular, Gemini's native HTTP implementation withholds candidate endings through actual EOF/owned closure/final cancellation and raises LlmUpstreamException for a late native error/block. Actual HTTP status and the complete original error-frame body remain available; conversion does not replace them with a tool result or successful completion. If an error frame is instead supplied directly to the conversion codec, its existing semantic diagnostics still apply. These are different failure layers, not alternate successful outcomes.

### Explicit native transport beneath conversion

ConvertingLlmProvider delegates credential support to its fixed native provider and includes the native provider BindingIdentity in its own Session identity. A caller-configured [OpenAiLlmProvider HTTP transport](../Asgard.Loki.LLM.OpenAI/README.md#explicit-native-http-endpoints-and-authentication) therefore retains its operation paths/fixed queries, selected Bearer/header/query/Anonymous credentials and actual target binding when wrapped by this existing decorator. Conversion changes only the explicitly selected supported native protocol fields; it does not choose authentication or infer a protocol from a path.

```csharp
var native = new OpenAiLlmProvider(factory, new OpenAiHttpOptions
{
    Transport = new OpenAiHttpTransportOptions
    {
        ChatCompletionsPath = "native/chat.run?existing=1",
        Authentication = OpenAiHttpAuthentication.Query, CredentialName = "api_key"
    }
}, id: "native-http", httpClientName: "Explicit.Native.Http");
var provider = new ConvertingLlmProvider(native, LlmProtocol.ChatCompletions, "converted");
// Register this provider with the existing complete target, caller API Key,
// requested public protocol, Session store and coordinator; target.ProviderId = provider.Id.
```

| Configuration | Behavior / limits |
| --- | --- |
| Explicit upstream | Fixed selected Chat provider in this example; no server/path/model detection |
| Credential kind | Native None requires LlmCredential.Anonymous; other configured modes require API Key; AllowLoss cannot change support |
| Session | Any native path/auth/client/policy change invalidates the complete converted binding before another send |
| Native errors / ending | Native byte/status/media errors and EOF/closure/final-cancellation failures propagate; conversion cannot turn them into success |

Tests exercise public Responses/Messages/Gemini to fixed Chat with four native authentication modes over JSON/SSE, plus path-change Session rejection. They reuse independent manually maintained protocol fixtures and verify actual address/auth/model/tool correlation, immutable source input and complete native JSON in Conversion metadata. Complete native transport snapshot/boundary tests live in the OpenAI supplier tests. Production conversion code, policies, dependency direction and existing semantic limits are unchanged. Byte and native state budgets remain with the selected provider; conversion retains its separate existing bounds. No gateway configuration or automatic authentication/conversion workflow is added.

### Explicit native Messages transport beneath conversion

The same decorator composes with [AnthropicLlmProvider's explicit native Messages path and authentication](../Asgard.Loki.LLM.Anthropic/README.md#explicit-native-messages-endpoint-and-authentication). AnthropicHttpOptions.Authentication is the sole selector: ApiKey/Bearer consume API-Key identities, Header/Query add explicit native credential formatting, and None requires an explicit Anonymous identity. Native API version, operation path, fixed query, client and local policies remain in the complete native/converted Session binding.

```csharp
var native = new AnthropicLlmProvider(factory, new AnthropicHttpOptions
{
    Authentication = AnthropicAuthentication.Query,
    Transport = new AnthropicHttpTransportOptions
    {
        MessagesPath = "native/messages.run?existing=1", CredentialName = "api_key"
    }
}, id: "native-messages", httpClientName: "Explicit.Native.Messages");
var provider = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "converted-messages");
// Register the complete target, requested public protocol, selected credential and existing Session dependencies.
```

| Configured path | Native behavior / limits |
| --- | --- |
| Fixed native Messages upstream | Explicit public Chat/Responses/Gemini conversion; no path-driven protocol selection |
| Five native credential modes | Credential support is delegated unchanged; AllowLoss cannot enable another kind or combine authentication |
| Native errors and endings | Original raw HTTP/late native status/media/body failures propagate; message_stop/EOF/owned closure/final cancellation remain native gates |
| Changed operation/auth context | Existing converted Session fails before another HTTP send |

Tests exercise three public protocols to fixed native Messages over JSON/SSE and all five credential modes, plus one endpoint-change binding test per mode. They reuse independent manually maintained conversion samples and check the actual URI/auth/version/model/tool correlation, immutable caller and complete native JSON in Conversion metadata. Native complete byte/field/SSE snapshots and fault boundaries remain in the supplier tests. No real deployment/model/setting validation is implied; production conversion algorithms, options and dependencies are unchanged. There is no token discovery, refresh, tool execution, hidden history or gateway routing.

### Explicit native Gemini transport beneath conversion

The existing decorator also composes with [GoogleLlmProvider's explicit native Gemini model paths and authentication](../Asgard.Loki.LLM.Google/README.md#explicit-native-gemini-endpoints-and-authentication). Native ApiKey/Bearer/Header/Query modes use the selected API-Key identity; native None requires explicit Anonymous support. Credential support and provider binding are delegated unchanged. Operation paths, escaped actual model or explicit fixed-model declaration, auth formatting, client and local native policies remain with the selected provider and complete target.

```csharp
var native = new GoogleLlmProvider(factory, new GoogleHttpOptions
{
    Authentication = GoogleHttpAuthentication.Query,
    Transport = new GoogleHttpTransportOptions
    {
        GenerateContentPath = "native/{model}:generate?existing=1",
        StreamGenerateContentPath = "native/{model}:stream?alt=sse&existing=1",
        CredentialName = "api_key"
    }
}, id: "native-gemini", httpClientName: "Explicit.Native.Gemini");
var provider = new ConvertingLlmProvider(native, LlmProtocol.Gemini, "converted-gemini");
// Register the complete target, requested public protocol, selected credential and existing Session dependencies.
```

| Configuration | Behavior / limits |
| --- | --- |
| Fixed native Gemini protocol | Explicit public Chat/Responses/Messages conversion; no endpoint-driven protocol selection |
| Selected native model | Native path escaping/fixed declaration remains provider-owned; no body model/stream injection |
| Five credential modes | Support is delegated; AllowLoss cannot enable another kind or combine credentials |
| Native failure / Session | Original raw HTTP/late-native status/media/body and candidate terminal gates propagate; path/auth/model context changes invalidate complete binding |

Tests run three public protocols against actual native Gemini JSON/SSE over all five authentication modes, plus per-mode endpoint-change Session rejection. They reuse independent manually maintained conversion fixtures and verify actual URI/auth/model/tool correlation, immutable source input and complete native JSON retained in Conversion metadata. Complete native bytes/fields/model escaping/candidate/closure snapshots remain in the Google supplier tests. Production conversion algorithms/options/dependencies are unchanged. No provider-peer dependency, token discovery, tool execution, hidden history or gateway routing is introduced.
