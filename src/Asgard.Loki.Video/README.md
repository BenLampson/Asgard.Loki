# Asgard.Loki.Video

Independent native video task contracts for the pure .NET SDK. Depends only on `Asgard.Loki.Abstractions`; Video owns its results, lifecycle and complete-target references.

| Entry | Behavior |
| --- | --- |
| `SubmitAsync(VideoTaskRequest)` | Snapshot native parameters, select one complete target, submit once and return original result plus persistent reference |
| `GetAsync(VideoTaskReference)` | Query the original target once and return native status, errors, metadata and result URLs |
| `SubmitAsync(VideoMultipartTaskRequest)` | Optional original text/file multipart submission with borrowed input streams |
| `RemixAsync(reference, request)` | Optional explicit native remix on the original complete target; return a new task reference |
| `CopyContentAsync(reference, destination, request)` | Optional explicit native content request copied into a borrowed writable stream |

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

`Parameters` keeps the selected supplier's native JSON, not a universal prompt/media representation. Root `model` is reserved for the configured actual target. Unknown fields, native asset references, null/false/zero and numeric precision remain intact; input is copied before waits.

Optional native transports use independent interfaces: `IVideoMultipartTaskAdapter`, `IVideoRemixTaskAdapter` and `IVideoTaskContentAdapter`. Existing JSON-only targets remain valid. Selecting a target without the required interface fails explicitly; the SDK does not try another target or convert protocols. Actual supplier support is documented in each provider package.

`VideoMultipartTaskRequest.Fields` retains ordered, repeated original text fields; objects/arrays are never implicitly flattened or stringified. `Uploads` retains each original file field, filename, content type and borrowed stream. Model is reserved for the configured target. Metadata is snapshotted before waits; streams are read from their current positions without seeking, rewinding, media decoding or ownership transfer. Reusing one stream for multiple parts is rejected. Callers keep upload streams valid and avoid concurrent use until the call finishes.

`RemixAsync` resolves the source reference without new target selection, requires its public model to match and returns a new task ID bound to the same target. It performs one native call without querying source status or changing the original reference. Native source relationships and other fields stay in the supplier result.

`CopyContentAsync` makes one explicit native content request on the original target. `VideoContentRequest.Query` preserves ordered native query fields and opaque values; suppliers own URL encoding and supported query semantics. `VideoContentResult` retains task association, bytes written, original HTTP status and independent multivalue headers. The SDK borrows the destination at its current position and does not close, seek, truncate or implicitly flush it. Failure can leave partial bytes already written; callers choose whether to discard or commit those bytes. Content copying is not a new generation completion event, and there is no automatic content read after task success.

`IVideoTaskAdapter.BindingIdentity` must cover server, actual model, credential identity, native operation, implementation and local policies. `VideoTaskReference` binds the public route, target ID, fingerprint and original task ID without raw credentials. Applications can persist it as JSON. Rebuilding a client/reordering targets cannot move an existing task. Removed/changed configurations fail before IO; returning another task's ID fails explicitly. This is routing consistency, not an authorization token; the upstream enforces access. Adapter instances remain borrowed.

`Snapshot.NativeStatus` and complete `Data` retain native semantics, including errors and result URLs. Only known statuses map to `VideoTaskState`; accepted submission, HTTP 200 and unknown states do not prove success. Calls perform no retry, failover, automatic query or content transfer, URL refresh, callback hosting, asset synchronization or format conversion. Content is copied only through an explicit caller-requested operation. Queries are caller-driven.

Core offline tests cover snapshots, persistent target identity/reconstruction, changed or removed targets, wrong task IDs, no failover and borrowed ownership. Independent native fixtures verify original task fields and status boundaries. No live video verification is claimed; concrete supplier support is documented individually.
