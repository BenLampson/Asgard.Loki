# Asgard.Loki.Video.Midjourney

Client calls for an explicitly configured **unofficial Midjourney proxy** video contract. This package references Video, Infrastructure and `Microsoft.Extensions.Http`. It provides no Midjourney-hosted API address, proxy server, Discord connection or account pool.

| Native operation | SDK behavior |
| --- | --- |
| `POST submit/video` | Submit original generation or extension JSON once; return original acknowledgement and task reference |
| `GET task/{id}/fetch` | Query the originally bound task once; retain native status and all video results |

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.Midjourney;

// factory, proxyApiRoot, actualVideoType and apiSecret belong to the application.
// proxyApiRoot includes the API prefix, for example https://proxy.example/mj/.
var target = new MidjourneyVideoAdapter(factory, new MidjourneyVideoOptions
{
    Id = "midjourney-video", BaseAddress = proxyApiRoot,
    VideoType = actualVideoType, ApiSecret = apiSecret
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
        ["image"] = "https://media.example/first.png",
        ["motion"] = "low", ["loop"] = false, ["batchSize"] = 2
    }
}, cancellationToken);
// Persist submitted.Reference if needed. Each query runs once.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
var originalEnvelope = current.Snapshot.Data;
```

For an extension, explicitly submit the native parent reference and index after the parent has completed:

```csharp
var parameters = new JsonObject
{
    ["prompt"] = "Continue the boat's movement.",
    ["action"] = "extend", ["taskId"] = completed.Reference.TaskId,
    ["index"] = 0, ["motion"] = "low", ["loop"] = false
};
// Pass parameters to a new VideoTaskRequest on the original configured target.
```

The required root is the complete HTTP/HTTPS proxy API root, including `mj/` or an explicit speed prefix such as `mj-fast/mj/`. The SDK appends operation paths without selecting a proxy, guessing a speed mode or rewriting the supplied scheme/prefix/port. Required `VideoType` fixes the actual native `videoType` for this target; there is no default model/type catalog. SDK public route `Model` remains routing context and is never sent as a proxy model field. Request `videoType` aliases, including different case, are rejected so the upstream's case-insensitive field binding cannot override the target. The configured type is the only field injected into original JSON.

`ApiSecret=null` explicitly selects anonymous calls. A nonnull secret must be nonblank printable ASCII and is sent unchanged in `mj-api-secret`; no Authorization header, key-prefix detection, cookie/login flow or credential discovery is added. The full target identity includes root, native video type, operation/implementation, anonymous or credential identity, named HTTP client and byte limits. Persisted [Video references](../Asgard.Loki.Video/README.md) reject drift before query IO. The factory remains borrowed; configure its named client without automatic retry, hedging or redirects.

Original `prompt`, `image`, `endImage`, `motion`, `loop`, `batchSize`, `state`, `notifyHook`, extension `action/taskId/index` and unknown fields remain unchanged, including false/null/zero and numeric precision. Parameters are copied before the first wait. There is no prompt flag stripping, translation, media fetch/rewrite/upload, setting default or automatic parent lookup. The application supplies the correct parent and original output index, including index zero; the upstream enforces parent status and account access. Type/media compatibility, enabled video support and model availability remain properties of the chosen proxy/account. See the proxy author's [video request contract](https://github.com/trueai-org/midjourney-proxy/blob/b68b2baa6667fa14306e521205ff687f9e10d83f/src/Midjourney.Base/Dto/SubmitVideoDTO.cs), [native task fields](https://github.com/trueai-org/midjourney-proxy/blob/b68b2baa6667fa14306e521205ff687f9e10d83f/src/Midjourney.Base/Models/TaskInfo.cs) and [authentication configuration](https://github.com/novicezk/midjourney-proxy/blob/main/docs/config.md).

Acknowledgements accept native integer codes `1` (submitted), `21` (existing task) and `22` (queued), require the original string task ID in `result`, and retain the original code. Optional `properties.status` maps its current state, including an already completed existing task; without it, code 22 is queued and codes 1/21 are submitted with no invented native status. Other codes are original API failures, even if a task ID appears in the error. Query requires the exact original string `id` and native `status`; an explicitly returned action must identify a VIDEO task.

Known `NOT_START`, `SUBMITTED`, `IN_PROGRESS`, `FAILURE`, `SUCCESS` and `CANCEL` map to queued, submitted, running, failed, succeeded and cancelled. `MODAL` and future states remain unknown and nonterminal; progress 100% alone cannot complete a task. Success requires original `videoUrl` and/or a nonempty `videoUrls` list with a valid URL on every element. A legacy `imageUrl` can carry video for a confirmed VIDEO task; query fallback requires native `action=VIDEO`, while the video submission acknowledgement already supplies that operation context. No file extension is used to guess the media type. All URLs, list order, thumbnails, buttons, native state/errors/timestamps, properties and unknown metadata remain in complete `Data`; no video URL is synthesized from another field. Contradictory success/failure or malformed output lists fail explicitly.

Snapshots retain original HTTP status/body/content type separately in `HttpResponse`. HTTP and native API failures preserve original bytes in `VideoUpstreamException`; malformed JSON, encoding, duplicate members, missing/null tasks, mismatched IDs and wrong task types fail explicitly. Request JSON defaults to 64 MiB UTF-8, raw response/error bytes to 16 MiB; these configurable transport bounds do not guarantee upstream acceptance. Complete bounded reading, declared length, resource disposal and final cancellation precede delivery. Per-call resources are disposed; borrowed factories remain application-owned.

No automatic retry, failover, background polling, timeout-to-failure rule, callback receiver, media download, output-URL refresh, credential forwarding to media URLs, image coordination, account/storage administration or billing service is provided. Validation uses independent original JSON fixtures and offline substituted HTTP handlers only. No real proxy/account, model/media acceptance, video output or URL retrieval has been verified.
