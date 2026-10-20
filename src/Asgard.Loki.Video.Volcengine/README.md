# Asgard.Loki.Video.Volcengine

Native Volcengine Ark video generation for the pure .NET SDK. References the independent Video capability and `Microsoft.Extensions.Http`.

| Operation | Native API | Result |
| --- | --- | --- |
| `SubmitAsync` | `POST contents/generations/tasks` | Original acknowledgement and task ID |
| `GetAsync` | `GET contents/generations/tasks/{id}` | Original status, errors, metadata and result URLs |

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

Calls perform one submission or query. There is no polling, failover, media download or URL refresh. Result URL availability and expiry are controlled by Ark. Asset/group management uses a separate native authentication contract; a video API Key does not grant AK/SK management access.

Request and response limits default to 64 MiB and 16 MiB respectively. Invalid UTF-8/JSON, duplicate properties, wrong task IDs, truncated or oversized bodies and late read/disposal/cancellation failures fail explicitly. Offline xUnit v3 tests use independent fixtures and substitute transports. No real Seedance generation, asset preprocessing or media result has been verified by these tests.

Native references: [create task](https://docs.volcengine.com/docs/ark/create-video-generation-task-api?lang=zh), [get task](https://docs.volcengine.com/docs/ark/get-video-generation-task-api?lang=zh).
