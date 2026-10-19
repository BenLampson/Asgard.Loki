# Asgard.Loki.Image.OpenAI

OpenAI native image generation and editing provider for Asgard.Loki.

| Feature | Support |
| --- | --- |
| `POST /v1/images/generations` | Yes |
| Complete JSON results | Yes |
| SSE partial/final image events | Yes, when supported by the upstream model |
| Base64 and URL outputs | Preserved; Base64 decoding is available on demand |
| Quality, size, transparency, output format, image count and future parameters | Passed through |
| `POST /v1/images/edits` JSON | Yes, explicit native `images` references and optional `mask` object |
| `POST /v1/images/edits` multipart | Yes, ordered `image[]` uploads and optional separate `mask` upload |
| Editing SSE | Yes, explicit native JSON/multipart stream methods; nonstream editing rejects `stream=true` |
| Image variations | Not implemented |

Create `OpenAiImageAdapter(IHttpClientFactory, OpenAiImageOptions)` and register it in `ImageOptions.Routes`. Configure the actual upstream model and API Key in the adapter options; use the route alias in requests. Optional organization/project headers and a custom base address are supported.

The default named HTTP client is `Asgard.Loki.Image.OpenAI`. Disable generation POST retries and hedging. Prompts and unknown JSON fields are not rewritten. Incomplete streams fail instead of claiming completion. URLs are returned without automatic downloads.

Local tests verify request/response and SSE behavior. No live image generation or provider entitlement check is performed by those tests.

## Native image editing

Create `OpenAiImageEditAdapter(IHttpClientFactory, OpenAiImageOptions)` and register it separately in `ImageOptions.EditRoutes`. The configuration type is shared with generation; the adapter/target registration is explicit and no generation target is borrowed. Public route aliases stay separate from actual upstream models. Both edit methods send one POST to `images/edits` under the configured versioned base address and preserve configured Bearer/organization/project headers.

```csharp
var edit = new OpenAiImageEditAdapter(httpFactory, new OpenAiImageOptions
{
    Id = "image-edit", Model = actualImageModel, ApiKey = apiKey
});
await using var loki = new LokiClientBuilder().AddImage(new ImageOptions
{
    EditRoutes = [new() { Model = "pictures", Targets = [edit] }]
}).Build();
var result = await loki.Image.EditAsync(new ImageEditRequest
{
    Model = "pictures", Prompt = "Keep the subjects and change the background.",
    Parameters = new()
    {
        ["images"] = JsonSerializer.SerializeToElement(new[] { new { image_url = sourceUrl } }),
        ["mask"] = JsonSerializer.SerializeToElement(new { file_id = uploadedMaskId }),
        ["output_format"] = JsonSerializer.SerializeToElement("webp")
    }
}, cancellationToken);
```

Import `Asgard.Loki`, `Asgard.Loki.Image`, `Asgard.Loki.Image.OpenAI` and `System.Text.Json`. JSON references carry exactly one native `image_url` or `file_id`; attached unknown fields retain their types and values. File IDs/URLs/data URLs are passed unchanged, without downloading, uploading/resolving files, inventing MIME types or converting to multipart. Native model/account reference eligibility remains upstream-owned.

For files, explicitly call `loki.Image.EditMultipartAsync(new ImageMultipartEditRequest { ... }, cancellationToken)` with caller-owned `ImageUpload` entries and optional separate `Mask`. Images use native repeated `image[]` parts in their original order; mask uses `mask`. Literal `ImageMultipartField` entries retain ordered/repeated unknown names, bracket syntax, empty strings and raw text. Known scalar duplicates, model/prompt/image/mask overrides, invalid UTF-8/header controls, reused stream objects and unreadable inputs fail before HTTP. Optional content types are validated as MIME values, never guessed from file names.

The caller keeps streams alive through completion. Uploads start at their current positions, do not query length/position, rewind, close, decode, resize or validate mask pixels/dimensions. The official masked-edit contract associates a mask with the first image; this SDK preserves that native ordering and separate mask role. It never remaps masks or checks their visual effect. Arbitrary fixture bytes in tests are not accepted image-format evidence.

Neither nonstream method injects `stream`, quality, size, format or other native defaults. Explicit false remains false; true must use the dedicated edit streaming methods below and is rejected by nonstream methods. There is no automatic JSON/multipart fallback, generation fallback, retry or failover. Disable POST retries/hedging and unintended replay in the application-owned HTTP pipeline; multipart parts also reject a second serialization of borrowed streams.

Complete responses retain native JSON/GetRawText, ordered Base64/URL items, revised prompts, exact numbers, usage and unknown fields. Known response container/image fields and duplicate JSON members are validated without decoding images or recalculating usage. A successful result is returned only after normal body EOF, declared-length verification and response/client disposal. Invalid UTF-8, unexpected SSE/media, tail errors, cancellation or disposal failure do not return success. `ImageEditUpstreamException` retains actual HTTP status, original error bytes and content type, including non-UTF-8 errors and native business errors in 2xx responses. Existing generation exception behavior is unchanged.

The [official image guide](https://developers.openai.com/api/docs/guides/image-generation) documents file/mask editing; the official endpoint OpenAPI exposes JSON reference requests, multipart curl examples and native partial/completed edit events. There are 119 offline nonstream edit cases, 132 editing SSE cases, five original generation cases and 94 additional generation regressions (350 total). Eight independent complete request goldens (generation/editing JSON/MIME), full native response/event fixtures and failure/ownership/encoding boundaries use substitute HTTP handlers only. Variations, actual OpenAI image/account acceptance and image/mask quality remain unverified or pending; no real OpenAI requests are made.

## Native editing event streams

The same explicitly registered `OpenAiImageEditAdapter` implements `IStreamingImageEditAdapter`. Call `loki.Image.StreamEditAsync(ImageEditRequest, token)` for native JSON references or `StreamMultipartEditAsync(ImageMultipartEditRequest, token)` for uploads. Every target in the edit route must expose that optional interface. The methods add native `stream=true` only when absent, preserve explicit true and reject an explicit false instead of overwriting it. All other settings remain caller-owned. Inputs are snapshotted at method invocation; HTTP clients/content/uploads are created or consumed only when enumeration starts. Each returned enumerable supports one execution; a second enumeration fails without another HTTP request or upload.

```csharp
await foreach (var item in loki.Image.StreamEditAsync(editRequest, cancellationToken))
{
    // Keep native fields and event identity; the SDK does not assemble or decode images.
    JsonElement native = item.Data;
    bool completed = item.IsCompleted;
}
```

`image_edit.partial_image` and `image_edit.completed` retain full JSON/GetRawText, original Base64, optional native image settings, usage and unknown fields. SSE name/id/retry remain independent delivery metadata, including standard id/retry inheritance. Partial indices and known counters are checked as exact nonnegative mathematical integers without floating-point rounding, narrowing, allocating by their value, adding usage totals or imposing a model-specific maximum. Unknown events remain intact and never gain a completion marker merely from their name. Partial images are independent native payloads; there is no image/text reconstruction, invented output association, index renumbering or requirement to receive the requested number of partial images.

One native completed event is held until strict UTF-8/SSE EOF, declared-length verification and source/response/client disposal all succeed. Empty/missing/duplicate terminals, late data, malformed/truncated frames, generic `[DONE]`, conflicting event/type, read/disposal/cancellation and errors after a held terminal cannot first publish success. Trailing comments do not become data events. A native error raises `ImageEditUpstreamException` with the original JSON data payload bytes and optional `EditEvent` retaining full JSON, target and SSE identity. Ordinary HTTP errors keep exact original body bytes and leave EditEvent null. A non-SSE JSON result is never converted into fake streaming events. Early stop closes the enumerator/transport while caller uploads and factory remain open. There is no reconnect, resume or replay.

The stream examples/fixtures establish the documented single-completion native protocol; they do not certify actual model/account settings, multiple-output streaming semantics, valid image encoding or visual quality. Generation has separate native fixtures and the safeguards below; editing SSE tests alone do not certify generation.

## Native generation boundaries

Existing `OpenAiImageAdapter` initialization and public generation methods stay available. Generation JSON/SSE now snapshot native request values before creating a client; the public SDK also snapshots before enumeration instead of rereading caller dictionaries/documents later. Only the actual model is rebound. Streaming adds true when absent, preserves true and rejects explicit false. Nonstreaming preserves omitted/false and rejects true. Neither silently overwrites a caller transport choice or injects quality/format/default fields. This removes the older unconditional stream-field overwrite. Each native generation enumerable executes once; repeated enumeration fails without replay.

Complete generation results require native UTF-8 JSON, full EOF/declared-length checks and successful source/response/client disposal. Original image order, URLs/Base64, revised prompts, usage, exact numbers and unknown JSON remain intact. Invalid containers/duplicate members and native business errors in 2xx responses fail rather than returning a misleading empty result.

Native `image_generation.partial_image` events and unknown events remain incremental. The original `image_generation.completed` is held until normal EOF and all transport resources close. Duplicate/late data, truncated/malformed frames, invalid UTF-8/length, cancellation and disposal/read failures cannot first expose completion. Generic `[DONE]` is rejected instead of stopping parsing early. Standard native error envelopes and the previously recognized `image_generation.failed` label now raise a failure instead of being delivered as an ordinary successful end. Partial payloads are not assembled, renumbered or matched to invented output identities; fewer/no partial images remain valid.

`ImageUpstreamException` preserves its original three-argument text constructor, StatusCode and ResponseBody surface. A new byte-body constructor and `Body`/`ContentType` retain exact native error bytes; optional `GenerationEvent` carries original SSE JSON/target/name/id/retry. ResponseBody returns original text for the legacy constructor; for byte bodies it explicitly decodes strict UTF-8 and throws on invalid encoding, so inspect Body for unknown/binary errors. Ordinary HTTP/JSON failures leave GenerationEvent null. No decoder replaces native error bytes.

Ninety-four additional cases compare separate complete generation JSON/SSE request, result and event fixtures, including Unicode fragmentation, unknown fields and caller document disposal. They exercise native errors after held completion, JSON/SSE media refusal, no retry/failover, EOF/length/read/disposal/cancellation, single enumeration and generation/edit streams consumed in an interleaved order. Only private pure image JSON/number/byte validators are shared inside Image.OpenAI; each operation owns its native stream state and exception policy. No Audio/LLM dependency, new coordinator, network call or live image-quality claim is introduced.
