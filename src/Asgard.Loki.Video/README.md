# Asgard.Loki.Video

Independent native video task contracts for the pure .NET SDK. Depends only on `Asgard.Loki.Abstractions`; Video owns its results, lifecycle and complete-target references.

| Entry | Behavior |
| --- | --- |
| `SubmitAsync(VideoTaskRequest)` | Snapshot native parameters, select one complete target, submit once and return original result plus persistent reference |
| `GetAsync(VideoTaskReference)` | Query the original target once and return native status, errors, metadata and result URLs |

```csharp
// target is an IVideoTaskAdapter configured with its own complete upstream identity.
await using var loki = new LokiClientBuilder().AddVideo(new VideoOptions
{
    Routes = [new VideoRoute { Model = "video", Targets = [target] }]
}).Build();
var submitted = await loki.Video.SubmitAsync(new VideoTaskRequest
{
    Model = "video",
    Parameters = new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "A slow camera move." })
    }
}, cancellationToken);
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
var originalResult = current.Snapshot.Data;
```

`Parameters` keeps the selected supplier's native JSON, not a universal prompt/media representation. Root `model` is reserved for the configured actual target. Unknown fields, native asset references, null/false/zero and numeric precision remain intact; input is copied before waits. Multipart/upload transports require explicit contracts.

`IVideoTaskAdapter.BindingIdentity` must cover server, actual model, credential identity, native operation, implementation and local policies. `VideoTaskReference` binds the public route, target ID, fingerprint and original task ID without raw credentials. Applications can persist it as JSON. Rebuilding a client/reordering targets cannot move an existing task. Removed/changed configurations fail before IO; returning another task's ID fails explicitly. This is routing consistency, not an authorization token; the upstream enforces access. Adapter instances remain borrowed.

`Snapshot.NativeStatus` and complete `Data` retain native semantics, including errors and result URLs. Only known statuses map to `VideoTaskState`; accepted submission, HTTP 200 and unknown states do not prove success. Calls perform no retry, failover, automatic query, download, URL refresh, callback hosting, asset synchronization or format conversion. Queries are caller-driven.

Core offline tests cover snapshots, persistent target identity/reconstruction, changed or removed targets, wrong task IDs, no failover and borrowed ownership. Independent native fixtures verify original task fields and status boundaries. No live video verification is claimed; concrete supplier support is documented individually.
