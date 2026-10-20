# Asgard.Loki.Video.Ali

Native Alibaba Cloud DashScope video tasks for the pure .NET SDK. References Video, Infrastructure's bounded byte reader and `Microsoft.Extensions.Http`; no other capability package is required.

| Configured operation | Native submission | Query/result |
| --- | --- | --- |
| `AliVideoOperation.VideoGeneration` | `POST services/aigc/video-generation/video-synthesis` | `GET tasks/{task_id}` |
| `AliVideoOperation.ImageToVideo` | `POST services/aigc/image2video/video-synthesis` | `GET tasks/{task_id}` |

Choose the operation explicitly to match the actual model's native API. Model names never select or change the service. Matching text/image inputs, first/last frames and speech-driven inputs remain their original native JSON. The SDK does not infer model capabilities or replace one input shape with another.

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.Ali;

// factory, model and apiKey belong to the application.
var target = new AliVideoAdapter(factory, new AliVideoOptions
{
    Id = "wan-video", Model = model, ApiKey = apiKey,
    Operation = AliVideoOperation.VideoGeneration
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
        ["input"] = new JsonObject { ["prompt"] = "A paper boat on a lake." },
        ["parameters"] = new JsonObject { ["duration"] = 5, ["watermark"] = false }
    }
}, cancellationToken);
// Persist the reference if later queries must survive application restart.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
var originalOutput = current.Snapshot.Data.GetProperty("output");
```

The default root is `https://dashscope.aliyuncs.com/api/v1/`. Set `BaseAddress` explicitly for the desired region or workspace endpoint, including its API path. The model, endpoint and API Key must belong to the matching upstream region/workspace. Each target binds server, actual model, credential identity, operation, client name, implementation and resource policy. Existing task references reject configuration drift before query IO. See the [Video contract](../Asgard.Loki.Video/README.md).

`SubmitAsync` sends Bearer authentication, JSON content and `X-DashScope-Async: enable`. `GetAsync` performs one authenticated GET. A shared task-query endpoint does not loosen the original complete-target binding. Direct adapter calls are also available against the explicitly configured target.

`Parameters` contains original root fields except reserved `model`, which comes from target configuration. `input` must be an object; an optional `parameters` member must be an object. Nested media URLs, settings, unknown fields, null/false/zero and numeric precision remain intact. Inputs are copied before waits. The SDK does not fetch input media, upload files, inject model settings or certify every forwarded field's compatibility with the selected model.

The complete native envelope remains in `VideoTaskSnapshot.Data`, including `request_id`, `output`, original errors, usage and unknown members. Native `PENDING`, `RUNNING`, `SUCCEEDED`, `FAILED` and `CANCELED` map to their known lifecycle values. `UNKNOWN` and future values remain unknown and nonterminal; they do not prove expiry or failure. A known successful task must include a nonempty original `output.video_url` or `output.results.video_url`. URLs and their expiry remain upstream data, never downloaded or refreshed by the SDK.

HTTP/API failures throw `VideoUpstreamException` with original status, content type and copied raw response bytes, including native API errors returned with HTTP 200. A native failed task remains a result with its `output.code/message`; it is not confused with successful generation or a transport exception. Missing task identity/status, mismatched task IDs, malformed JSON/UTF-8, duplicate members and invalid success results fail explicitly.

Request and response limits default to 64 MiB and 16 MiB. The SDK borrows the factory, creates/disposes per-call clients and messages, and waits for complete body reading and all resource disposal before returning a result. Configure the named client `Asgard.Loki.Video.Ali` without automatic generation retries, hedging or redirects when one-shot submission is required. There is no SDK polling, failover, background worker, callback host, media downloader or task cache.

Offline xUnit v3 tests cover independent native request/result fixtures, explicit operations, persistent target binding, original lifecycle/errors, input snapshots, strict transport boundaries and final cancellation/disposal. No real Wan generation, media processing or account/model availability has been verified.

Native references: [text-to-video](https://help.aliyun.com/zh/model-studio/text-to-video-api-reference), [legacy text-to-video](https://help.aliyun.com/zh/model-studio/legacy-wan-text-to-video-api-reference), [speech-driven video](https://help.aliyun.com/zh/model-studio/wan-s2v-api).
