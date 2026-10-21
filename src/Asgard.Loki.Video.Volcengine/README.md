# Asgard.Loki.Video.Volcengine

Native Volcengine Ark and Jimeng Visual video generation for the pure .NET SDK. References the independent Video capability, Infrastructure's bounded byte reader and `Microsoft.Extensions.Http`.

| Operation | Native API | Result |
| --- | --- | --- |
| `VolcengineVideoAdapter.SubmitAsync` | `POST contents/generations/tasks` | Original acknowledgement and task ID |
| `VolcengineVideoAdapter.GetAsync` | `GET contents/generations/tasks/{id}` | Original status, errors, metadata and result URLs |
| `JimengVideoAdapter.SubmitAsync` | Signed `POST /?Action=CVSync2AsyncSubmitTask&Version=2022-08-31` | Original code/data/task ID |
| `JimengVideoAdapter.GetAsync` | Signed `POST /?Action=CVSync2AsyncGetResult&Version=2022-08-31` | Original code/data/status and video URL; optional original query parameters |

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.Volcengine;

// factory is the application's IHttpClientFactory; model and apiKey are caller-owned.
var target = new VolcengineVideoAdapter(factory, new VolcengineVideoOptions
{
    Id = "ark-video", Model = model, ApiKey = apiKey
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
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "A slow camera move." }),
        ["duration"] = 5,
        ["watermark"] = false
    }
}, cancellationToken);
// Persist submitted.Reference if the application needs to query after restart.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
if (current.Snapshot.State == VideoTaskState.Succeeded)
{
    var videoUrl = current.Snapshot.Data.GetProperty("content").GetProperty("video_url").GetString();
}
```

The default root is `https://ark.cn-beijing.volces.com/api/v3/`. Specify the actual model or inference endpoint explicitly. Each target owns its root, model, API Key, implementation and byte limits; no LLM or audio credentials are borrowed. Configure the named HTTP client `Asgard.Loki.Video.Volcengine` through the borrowed factory. The SDK disposes clients created for calls, requests, responses and response streams, but never the factory. Disable retries, hedging and automatic redirects in the supplied transport when one-shot submission is required.

`Parameters` retains Ark's original `content`, media and `asset://` references, draft/settings/tools/callback fields and unknown members. Root `model` is reserved for the configured target. The SDK snapshots input before waits; it does not fetch input media, upload files or host callbacks. Model availability and parameter compatibility remain upstream decisions. Direct adapter calls are available against an explicitly configured target.

`VideoTaskReference` binds subsequent queries to the original complete target; server, model, credentials or resource-policy changes fail before query IO. The server's resolved model/version remains in the original result. References enforce routing consistency, not authorization. See the [Video contract](../Asgard.Loki.Video/README.md).

Known `queued`, `running`, `succeeded`, `failed`, `cancelled` and `expired` states map to Video lifecycle values. A submission acknowledgement is not generation success; unknown states remain nonterminal with original JSON intact. A known successful task must include its native `content.video_url`. HTTP/API errors throw `VideoUpstreamException` with original status, content type and copied body bytes; HTTP 200 task failures remain native task results, including their error object.

Calls perform one submission or query. There is no polling, failover, media download or URL refresh. Result URL availability and expiry are controlled by Ark.

## Native Jimeng Visual video

`JimengVideoAdapter` uses the native Visual root `https://visual.volcengineapi.com/`, version `2022-08-31`, signing region `cn-north-1` and service `cv`. Supply a full original `RequestKey` explicitly. It is the actual `req_key`, not a model alias inferred from image count or frame settings. Ark video and Visual use different endpoints/protocols; their credentials and result semantics remain independently configured.

```csharp
var visual = new JimengVideoAdapter(factory, new JimengVideoOptions
{
    Id = "visual-video", RequestKey = actualRequestKey,
    AccessKeyId = accessKeyId, SecretAccessKey = secretAccessKey
});
// Register visual as a VideoRoute target just like the Ark adapter above.
var submittedVisual = await visual.SubmitAsync(new VideoTaskRequest
{
    Model = "video",
    Parameters = new JsonObject
    {
        ["prompt"] = "A paper boat on a lake.", ["seed"] = -1, ["frames"] = 121
    }
}, cancellationToken);
var queriedVisual = await visual.GetAsync(submittedVisual.TaskId,
    new JsonObject
    {
        // Keep native req_json as a string; the SDK does not rewrite its contents.
        ["req_json"] = queryConfigurationJson
    }, cancellationToken);
```

The common `loki.Video.GetAsync(reference)` performs one query with the fixed `req_key` and original task ID. For extra native query fields, call the originally bound adapter's `GetAsync(taskId, JsonObject, cancellationToken)` overload. Multiple-target applications retain the adapter associated with `VideoTaskReference.TargetId`; this direct native call does not reselect or recover a target. Neither overload polls or resubmits.

Inputs remain original native JSON, including `image_urls`, `binary_data_base64`, prompt, seed, frames, aspect ratio, original query configuration and unknown fields. Root `model` is reserved by the Video submission contract; `req_key` is fixed by the complete target and cannot be supplied in submission parameters. Query extras cannot override `req_key` or `task_id`; optional `req_json` must remain a string. The SDK snapshots before waits, never changes a request key based on media, injects no generation defaults and neither fetches nor uploads input media.

The complete target fingerprint includes HTTPS root, actual request key, AK/SK/token identity, native version/region/service, implementation, HTTP client and byte limits. Persisted references reject drift before query IO. Optional temporary `SecurityToken` participates in the signature. Credentials are captured, excluded from configuration JSON and never discovered or refreshed. A borrowed `TimeProvider` supplies signing time; actual serialized bytes, native root path and Action/Version query are signed exactly once. The named client is `Asgard.Loki.Video.Volcengine.Jimeng`; configure it without automatic retries, hedging, redirects or default credential headers. The factory and clock remain application-owned.

Visual success is determined by outer integer `code=10000`, then native `data.status`. Submit acknowledgement means accepted, with no invented native status. Known query values `in_queue`, `generating`, `done` and `expired` map to queued, running, successful and expired states. `done` also requires a nonempty original `data.video_url`. Native `not_found` is ambiguous between absence and expiry, so it remains unknown/nonterminal; future statuses remain unknown as well. The redundant top-level `status` is preserved without interpreting it as a task lifecycle.

Native query responses commonly omit `task_id`. In that case `Snapshot.TaskId` carries the original requested ID as request context; `Data` is unchanged and gains no synthetic task/request-key field. This is not an independent upstream identity echo. If an optional task ID or request key is returned inside `data`, mismatches fail explicitly. All original request IDs, timings, output URLs, watermark metadata, errors and unknown members stay in the native JSON.

Non-success native codes, including processing/moderation failures, throw `VideoUpstreamException` with complete raw status/content-type/body. OpenAPI authentication errors and HTTP errors retain their original envelopes too. The SDK does not treat every non-success code as proof of permanent task failure. Missing/inconsistent known result shapes, malformed UTF-8/JSON, duplicate members and empty known-success URLs fail explicitly. No native error is converted into a successful task.

**Availability:** official [Visual video pages](https://docs.volcengine.com/docs/JimengAI/JimengAI-VideoGeneration30Pro-ProductIntroduction?lang=zh) mark older families as being phased out. Request-key availability and account access remain upstream constraints; no default model or automatic migration is supplied. Offline fixtures validate the documented native protocol and independent signatures, not model availability. **No real Visual generation, account authorization, watermark effect or media download has been verified.**

Request/response bounds are 64 MiB/16 MiB by default. Body reading, outer resource disposal and final cancellation complete before result delivery. The supplier shares private Volcengine signing/JSON helpers and Infrastructure's bounded byte reader, while Ark tasks, Visual tasks and asset operations keep their own protocol interpretation. There is no callback host, state cache, background poller, URL download/refresh, credential fallback or gateway endpoint.

References: [Visual native video](https://docs.volcengine.com/docs/JimengAI/JimengAI-VideoGeneration30Pro-InterfaceDocumentation?lang=zh), [official Visual actions](https://github.com/volcengine/volc-sdk-python/blob/master/volcengine/visual/VisualService.py), [official signing algorithm](https://github.com/volcengine/volc-sdk-python/blob/master/volcengine/auth/SignerV4.py).

## Private media asset library

`VolcengineAssetClient` exposes Ark's native inference asset/group operations in this supplier package. It uses independent AK/SK authentication against `https://ark.cn-beijing.volcengineapi.com/`, service `ark`, version `2024-01-01`. A video API Key does not grant management access. No extra capability package or shared asset abstraction is required.

| Public method | Native Action | Native body |
| --- | --- | --- |
| `CreateAssetGroupAsync` | `CreateAssetGroup` | Name, optional Description/GroupType/ProjectName |
| `CreateAssetAsync` | `CreateAsset` | GroupId, URL, AssetType, optional Name/ProjectName |
| `GetAssetGroupAsync` | `GetAssetGroup` | Id, optional ProjectName |
| `GetAssetAsync` | `GetAsset` | Id, optional ProjectName |
| `ListAssetGroupsAsync` | `ListAssetGroups` | Filter, optional paging/sort/ProjectName |
| `ListAssetsAsync` | `ListAssets` | Filter, optional paging/sort/ProjectName |
| `UpdateAssetGroupAsync` | `UpdateAssetGroup` | Id, optional Name/Description/ProjectName |
| `UpdateAssetAsync` | `UpdateAsset` | Id, optional Name/ProjectName |
| `DeleteAssetGroupAsync` | `DeleteAssetGroup` | Id, optional ProjectName |
| `DeleteAssetAsync` | `DeleteAsset` | Id, optional ProjectName |

Each method accepts the original `JsonObject` and returns `VolcengineAssetResult`, whose `Data` owns the complete envelope and `Result` exposes the native result object. Inputs are copied before waits; unknown fields, original names, nested values and number precision are preserved. The signed bytes are exactly the submitted JSON. Optional `SecurityToken` participates in signing. Credentials are captured at construction, excluded from options JSON and never discovered or refreshed implicitly. A borrowed `TimeProvider` can supply signing time; the default is `TimeProvider.System`.

```csharp
var assets = new VolcengineAssetClient(factory, new VolcengineAssetOptions
{
    AccessKeyId = accessKeyId,
    SecretAccessKey = secretAccessKey
});
var created = await assets.CreateAssetAsync(new JsonObject
{
    ["GroupId"] = groupId, ["URL"] = sourceUrl, ["Name"] = "Reference video",
    ["AssetType"] = "Video", ["ProjectName"] = projectName
}, cancellationToken);
var assetId = created.Result.GetProperty("Id").GetString()!;
var asset = await assets.GetAssetAsync(new JsonObject
{
    ["Id"] = assetId, ["ProjectName"] = projectName
}, cancellationToken);
if (asset.Result.GetProperty("Status").GetString() == "Active")
{
    // Use this URI in the matching native video content media field.
    var assetUri = "asset://" + assetId;
}
```

Asset creation starts upstream preprocessing; an Id or HTTP 200 does not establish readiness. Native `Processing`, `Failed`, errors and future statuses remain in `Result`. Only `Active` indicates readiness. API errors in `ResponseMetadata.Error` throw `VideoUpstreamException`; a failed asset inside `Result` remains data. Wrong operation/region/version/service, missing native result shapes and mismatched returned resource IDs fail explicitly.

List calls return exactly one page. Preserve `Filter` (including GroupType), project and sorting when passing the original opaque `NextToken` to the next explicit call. Alternatively provide PageNumber and PageSize together. The two paging modes cannot be mixed. The SDK does not inject project defaults, enumerate pages, synchronize assets or poll preprocessing. Use the same project for the group, asset and video inference endpoint/API Key. Availability, account permissions and group authorization are enforced by Ark. Group deletion also deletes its contained assets according to the native API; it happens only through an explicit delete call.

The asset named HTTP client is `Asgard.Loki.Video.Volcengine.Assets`. Resource ownership matches the video adapter. Root address, region, request and response byte limits are independently configurable; defaults are 1 MiB/16 MiB. The SDK sends URL references to Ark and never fetches source media or returned URLs. Asset URI use does not introduce a general storage service or VOD integration. No real asset upload, preprocessing, authorization or deletion has been verified.

Native references: [asset/group guide](https://docs.volcengine.com/docs/ark/private-virtual-avatar-library-guide-preview?lang=zh), [get asset](https://docs.volcengine.com/docs/ark/get-asset-api?lang=zh), [list assets](https://docs.volcengine.com/docs/ark/list-assets-api?lang=zh).

Request and response limits default to 64 MiB and 16 MiB respectively. Invalid UTF-8/JSON, duplicate properties, wrong task IDs, truncated or oversized bodies and late read/disposal/cancellation failures fail explicitly. Offline xUnit v3 tests use independent fixtures and substitute transports. No real Seedance generation, asset preprocessing or media result has been verified by these tests.

Native references: [create task](https://docs.volcengine.com/docs/ark/create-video-generation-task-api?lang=zh), [get task](https://docs.volcengine.com/docs/ark/get-video-generation-task-api?lang=zh).
