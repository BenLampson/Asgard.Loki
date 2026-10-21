# Asgard.Loki.Video.Vidu

Native Vidu video generation and task queries for the pure .NET SDK. This provider references Video, Infrastructure and `Microsoft.Extensions.Http`. Authentication, operation selection, native task states and results stay in this provider.

| Explicit operation | Submit | Query once |
| --- | --- | --- |
| `ViduVideoOperation.TextToVideo` | `POST ent/v2/text2video` | `GET ent/v2/tasks/{id}/creations` |
| `ViduVideoOperation.ImageToVideo` | `POST ent/v2/img2video` | Same task query |
| `ViduVideoOperation.StartEndToVideo` | `POST ent/v2/start-end2video` | Same task query |
| `ViduVideoOperation.ReferenceToVideo` | `POST ent/v2/reference2video` | Same task query |

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.Vidu;

// factory, actualModel and apiKey belong to the application.
var target = new ViduVideoAdapter(factory, new ViduVideoOptions
{
    Id = "vidu-text", Model = actualModel, ApiKey = apiKey,
    Operation = ViduVideoOperation.TextToVideo
});
await using var loki = new LokiClientBuilder().AddVideo(new VideoOptions
{
    Routes = [new VideoRoute { Model = "video", Targets = [target] }]
}).Build();
var submitted = await loki.Video.SubmitAsync(new VideoTaskRequest
{
    Model = "video",
    Parameters = new JsonObject
    {
        ["prompt"] = "A paper boat on a lake.",
        ["duration"] = 5, ["seed"] = 0, ["off_peak"] = false
    }
}, cancellationToken);
// Persist submitted.Reference if needed. Each explicit query runs once.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
var originalEnvelope = current.Snapshot.Data;
```

Choose image, start/end or reference generation explicitly and supply that endpoint's original JSON. Image order, data URLs, subject structures, native settings, callback URL, watermark options and unknown members remain unchanged. Native `payload` and `meta_data` strings remain strings, including embedded JSON text. Zero, false, null and numeric precision are preserved. The provider copies parameters before its first wait and supplies only the configured actual `model`; root `model` is reserved in request parameters. It does not infer the operation from image count or model text, rewrite media, remap a reference model name, strip empty/false fields or insert duration, resolution, movement or aspect-ratio defaults. Model-specific input shapes and supported combinations remain upstream constraints.

The default unversioned root is `https://api.vidu.com/`. Configure another HTTPS region/root or prefix explicitly; the provider appends `ent/v2` without gateway-prefix detection. Authentication is native `Authorization: Token {apiKey}` with the original key, including an opaque key starting with `sk-`; it never switches to Bearer based on that prefix. Blank/invalid header credentials fail before IO. The factory is borrowed; configure its named client without automatic retry, hedging or redirects. No login, environment discovery or gateway key interpretation is added. See official [text](https://platform.vidu.com/docs/api-reference/video-models/vidu-q1/text-to-video), [image](https://platform.vidu.com/docs/api-reference/video-models/vidu-q2/image-to-video), [start/end](https://platform.vidu.com/docs/api-reference/video-models/vidu-q2/start-end-to-video) and [reference](https://platform.vidu.com/docs/api-reference/video-models/vidu-q2/reference-to-video) contracts.

The complete target identity covers root, actual model, operation, credential digest, HTTP client, implementation and byte limits. Persisted [Video references](../Asgard.Loki.Video/README.md) reject drift before query IO, including operation changes even though the query endpoint is shared. Submit reads native `task_id`; query reads native `id` and requires an exact match to the original task. IDs remain original strings escaped as one path segment, without numeric conversion, trimming or synthetic native fields.

Native `created`, `queueing`, `processing`, `success` and `failed` map to submitted, queued, running, succeeded and failed. Future statuses stay unknown and nonterminal; an acknowledgement without a state means submitted, while queries require state. Known success requires a nonempty `creations` array with an original string ID and nonempty URL on every item, and cannot carry a nonempty/invalid `err_code`. This checks the result carrier and correlation, not generated media content. All creation IDs, original URLs, covers, watermark links, errors, passthrough data, credits and unknown fields remain in the complete `VideoTaskSnapshot.Data`; credits are not converted into LLM tokens. Signed output URLs remain unchanged and may expire according to the upstream contract.

HTTP errors and nonzero numeric native error codes, including HTTP 200 error envelopes, retain original bytes/status/content type in `VideoUpstreamException`. Malformed JSON, duplicate members, invalid UTF-8/escaped strings, wrong task IDs or incomplete success results fail explicitly. Request JSON defaults to a 20 MiB UTF-8 limit; raw response/error bytes default to 16 MiB. These are configurable SDK transport bounds, not a promise that an upstream model accepts that size. Cancellation reaches transport, and successful delivery waits for body completion, per-call resource disposal and a final cancellation check. The factory remains application-owned.

No automatic retry, failover, polling, callback receiver, media upload/download, subject-management API, task list/cancel or account/billing service is provided. Original subject/media references are only inference inputs. Tests use independent original JSON fixtures and substituted HTTP handlers; no live Vidu credentials, model availability, media acceptance, video quality or URL retrieval has been verified.
