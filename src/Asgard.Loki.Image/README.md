# Asgard.Loki.Image

[Azure](../Asgard.Loki.Image.Azure/README.md) supplies separately configured native deployment/version/API-Key JSON generation and multipart editing. It preserves original results and upload ownership, with native generation SSE and multipart editing SSE. Dated JSON-reference editing remains unsupported and fails locally without media/protocol conversion. Its edit completion marker describes the whole call; original per-image completed events remain intact.

Independent image generation/editing contracts, routing and module registration for Asgard.Loki.

Provides `AddImage()`, `.Image.GenerateAsync()` and `.Image.StreamAsync()`. Generation calls snapshot parameters at method invocation, including before stream enumeration. Requests retain arbitrary JSON parameters; results preserve full provider JSON, image data, URLs, usage and metadata. `GeneratedImage.Decode()` decodes Base64 on demand without downloading URL results.

Install providers separately: [OpenAI](../Asgard.Loki.Image.OpenAI/README.md), [Azure](../Asgard.Loki.Image.Azure/README.md) [Volcengine](../Asgard.Loki.Image.Volcengine/README.md) [Ali](../Asgard.Loki.Image.Ali/README.md) and [Google](../Asgard.Loki.Image.Google/README.md) document their own native operation support. This package depends only on the generic abstractions and does not reference LLM or audio.

## Native image editing contracts

`.Image.EditAsync(ImageEditRequest)` accepts native JSON settings/references; `.Image.EditMultipartAsync(ImageMultipartEditRequest)` accepts ordered caller-owned image uploads, an optional separate mask and literal multipart text fields. These are distinct named methods: the core never downloads URLs, resolves file IDs, Base64-encodes uploads or converts between the two inputs.

Configure `ImageOptions.EditRoutes` with application-owned `IImageEditAdapter` targets. Existing `Routes` and generation methods remain available; at least one operation needs a route. Generation and editing never borrow/filter each other's targets. Configuration is validated/copied at AddImage; each built ImageClient owns its selection state instead of sharing a prebuilt module. Each edit model route selects one complete target without retry, format fallback or failover.

```csharp
using Asgard.Loki;
using Asgard.Loki.Image;
using System.Text.Json;

IImageEditAdapter target = applicationOwnedNativeEditAdapter;
await using var loki = new LokiClientBuilder().AddImage(new ImageOptions
{
    EditRoutes = [new() { Model = "edit", Targets = [target] }]
}).Build();
var result = await loki.Image.EditAsync(new ImageEditRequest
{
    Model = "edit", Prompt = "Keep the subjects and change the background.",
    Parameters = new()
    {
        ["images"] = JsonSerializer.SerializeToElement(new[] { new { image_url = sourceUrl } }),
        ["mask"] = JsonSerializer.SerializeToElement(new { file_id = uploadedMaskId })
    }
}, cancellationToken);
```

Those JSON field names belong to the selected provider, not a universal image reference schema. The core clones JSON documents/dictionaries and rejects model/prompt overrides and Undefined. Native reference eligibility, formats and semantic settings are validated by the actual provider.

For multipart, supply at least one ImageUpload with readable Content, FileName and optional ContentType. A mask is another ImageUpload, not a file reference substituted into the image list. Each part must use an independent stream object; using the same stream in multiple image/mask slots fails before selection to prevent an empty second upload. Streams are excluded from JSON serialization, borrowed at the caller's current position and never read, rewound, length-inspected, opened by filename or disposed by the core. Keep them alive through the call/enumeration and do not concurrently consume them. Metadata/lists are independently copied; file names, image order, mask role and repeated/empty/native text fields remain intact. Text fields cannot override model/prompt/image/image[]/images/mask or contain header controls. No image decoding, resizing, mask alpha/dimension checks or pixel processing is performed.

Both operations return the existing ImageGenerationResult because the native images response container is shared: full JSON, ordered image items, revised prompt, URLs and usage stay unchanged. The name does not imply that editing fell back to text generation. GeneratedImage.Decode remains an explicit Base64 operation; URL results are never downloaded.

`IStreamingImageEditAdapter` optionally provides `.Image.StreamEditAsync` / `.StreamMultipartEditAsync`. Every target in a route must implement it before selection, with no filtering. ImageEditEvent clones full native JSON, metadata and SSE name/id/retry and carries an adapter-confirmed IsCompleted marker. Unknown events do not become success based on names. The core holds the original terminal event until source enumeration/disposal ends normally, rejects missing/duplicate/late events and surfaces tail/disposal/cancellation failures without first publishing success. Early stop releases the enumerator while uploads and adapters remain caller-owned. No completion is inferred from EOF, no text/image reassembly is added, and no connection is retried.

## Verification and provider status

96 offline core tests use custom in-memory adapters, authored complete JSON request/result fixtures and observed nonseekable streams. They verify snapshots, unknown/native fields, image/mask order and role, repeated fields, independent client/operation/model selection, no retry/failover, optional streaming gates, borrowed ownership, cancellation and success-after-source-end/disposal. They do not encode actual HTTP multipart, call OpenAI, decode real media or prove mask/pixel/model behavior.

The bundled OpenAI provider implements generation and separate native JSON/multipart nonstream and SSE editing via OpenAiImageEditAdapter; see its README for initialization, exact native fields and 119 nonstream plus 132 SSE editing cases. ImageEditUpstreamException preserves native error bytes/status/content type and optional original EditEvent for SSE failures. Generation keeps ImageUpstreamException and adds original byte/content-type/GenerationEvent access; its native JSON/SSE boundaries have 94 additional provider regressions. Variations remain unimplemented. The 96 core cases above do not substitute for provider wire or live image verification.

[Volcengine](../Asgard.Loki.Image.Volcengine/README.md) keeps Ark native image references/group/layer JSON, mixed image-level errors and distinct SSE outcomes. Its explicitly owned supplier-native request also represents omitted prompt without changing the common required-prompt convenience request; direct native calls do not select public routes. No per-image failure or summary is projected into another supplier protocol.

`ImageGenerationResult` and `GeneratedImage` also accept explicit supplier image/URL/Base64 views while retaining original root/item JSON. The original constructors and OpenAI-style data array convenience remain available. Provider lists are copied, including an explicit empty list for native text-only output; no original field is renamed or added.
