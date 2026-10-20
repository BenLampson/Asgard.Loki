# Asgard.Loki.Video.MiniMax

Native MiniMax video v1/v2 operations for the pure .NET SDK. References only Video, Infrastructure and `Microsoft.Extensions.Http`. Native version selection, authentication, status and result parsing stay in this provider.

| Explicit version | Submit | Query once | Result |
| --- | --- | --- | --- |
| `MiniMaxVideoApiVersion.V1` | `POST v1/video_generation` | `GET v1/query/video_generation?task_id={id}` | Original `file_id`; explicit `GetResultFileAsync(completedSnapshot)` retrieves its native video metadata |
| `MiniMaxVideoApiVersion.V2` | `POST v2/video_generation` | `GET v2/query/video_generation/{id}` | Original `task.content.url`, task metadata and usage |

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.MiniMax;

// factory, actualModel and apiKey belong to the application.
var target = new MiniMaxVideoAdapter(factory, new MiniMaxVideoOptions
{
    Id = "hailuo", ApiVersion = MiniMaxVideoApiVersion.V1,
    Model = actualModel, ApiKey = apiKey
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
        ["duration"] = 6, ["resolution"] = "768P", ["prompt_optimizer"] = false
    }
}, cancellationToken);
// Persist submitted.Reference if needed; each query runs exactly once.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
if (current.Snapshot.State == VideoTaskState.Succeeded)
{
    // Use the originally bound adapter. This is one explicit metadata call,
    // without re-querying the task or following the returned download URL.
    var result = await target.GetResultFileAsync(current.Snapshot, cancellationToken);
    var originalSignedUrl = result.DownloadUrl;
}
```

For v2, configure `ApiVersion = MiniMaxVideoApiVersion.V2` and supply the original `content` array rather than v1 flat fields:

```csharp
var parameters = new JsonObject
{
    ["content"] = new JsonArray(
        new JsonObject { ["type"] = "text", ["text"] = "Animate the fictional subject." },
        new JsonObject
        {
            ["type"] = "image_url", ["role"] = "first_frame",
            ["image_url"] = new JsonObject { ["url"] = "https://media.example/first.png" }
        }),
    ["duration"] = 5, ["resolution"] = "768P", ["ratio"] = "adaptive"
};
// Pass parameters to SubmitAsync on the explicitly configured v2 target.
// Successful queries already contain the original task.content.url.
```

The default unversioned root is `https://api.minimax.io/`. Set `BaseAddress` explicitly for another HTTPS region/root or proxy prefix; the SDK appends the chosen `v1`/`v2` path without stripping or guessing version segments. No default model or model catalog is provided. The model must support the selected native version and input shape; a model name never changes the configured endpoint. Current account/model availability and supported media combinations remain upstream constraints.

The complete target identity covers root, actual model, API Key digest, selected version, HTTP client, implementation and byte limits. Persisted Video references reject configuration drift before query IO. Original task IDs are preserved and escaped as one query parameter or path segment; a query returning another ID fails. See the [Video reference contract](../Asgard.Loki.Video/README.md).

`Parameters` contains original root fields except reserved `model`, which is supplied by target configuration. Native v1 prompt, frames, subject references and settings stay flat; native v2 text/image/video/audio `content`, roles, resolution, duration, ratio and settings remain unchanged. Unknown members, case, null/false/zero and numeric precision are preserved before network waits. A v2 `content` object array is required; model-specific media rules remain the upstream's responsibility. The SDK does not fetch input media, convert between versions, inject duration/resolution defaults or host callback URLs.

Submissions return `Submitted` with no invented native status. Queries retain the full original JSON in `VideoTaskSnapshot.Data`, including task errors, v2 usage, timestamps, dimensions, original links and unknown fields. Native v1 `Preparing`/`Processing` map to running, `Queueing` to queued, `Success` to success and `Fail` to failure. Native v2 `queued`, `running`, `succeeded`, `failed` and `cancelled` map to their known states. Future statuses remain unknown and nonterminal. Known success requires the matching version's result carrier: a v1 file ID or v2 `task.content.url`; it does not independently validate video content.

HTTP errors, nonzero native `base_resp.status_code` and root v2 API errors throw `VideoUpstreamException` with the original status, content type and raw bytes, including native errors returned with HTTP 200. A v1 `Fail` or v2 `task.error` on a failed task remains task data. v1 responses require a valid `base_resp`; v2 permits its omission. Malformed/duplicate JSON, invalid UTF-8, missing identities/status and incomplete known success results fail explicitly.

`GetResultFileAsync` accepts only a successful v1 video snapshot and revalidates its native task ID/status/file ID before IO. It performs one `GET v1/files/retrieve?file_id={id}`, requires the same file ID and `purpose=video_generation`, and returns `MiniMaxVideoResultFile` with full native metadata and the original signed `download_url`. File IDs use exact positive int64 decimal values without floating-point conversion; raw JSON types remain in `Data`. The supplied snapshot is an application-owned consistency record, not proof of account ownership; use the same bound adapter and let the upstream enforce access. Multiple-target applications retain the adapter associated with `VideoTaskReference.TargetId` for this provider-specific call. v2 uses its original content URL and does not fall back to v1 file retrieval.

The SDK does not download or refresh signed CDN links and never forwards the API Key to them. General file list/upload/delete/content APIs, task lists/cancellation/deletion/regeneration, H3-Context-IR, automatic polling, retries, failover, caching and callback hosting are outside these operations.

Request/response limits default to 64 MiB and 16 MiB. Configure the named client `Asgard.Loki.Video.MiniMax` without automatic retries, hedging or redirects. The factory is borrowed; each call owns newly created clients/messages/responses and waits for complete body reading, resource disposal and final cancellation checks before returning. Infrastructure supplies bounded raw-byte reads only; native protocol and lifecycle remain here.

Offline xUnit v3 tests cover independent native fixtures, v1/v2 inputs/results, unknown fields, original statuses/errors, persistent complete-target binding, task/file association, int64 precision, input snapshots, transport limits and final disposal/cancellation. **No real MiniMax generation, callback delivery, CDN download or account/model availability has been verified.**

References: [v1 generation](https://platform.minimax.io/docs/api-reference/video-generation-t2v), [v1 query](https://platform.minimax.io/docs/api-reference/video-generation-query), [v2 generation](https://platform.minimax.io/docs/api-reference/video-generation-v2-create), [v2 query](https://platform.minimax.io/docs/api-reference/video-generation-v2-query), [video result metadata](https://platform.minimax.io/docs/api-reference/file-management-retrieve).
