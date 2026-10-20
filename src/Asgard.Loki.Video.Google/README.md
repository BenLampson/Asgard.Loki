# Asgard.Loki.Video.Google

Native **Gemini Developer API** Veo video operations for the pure .NET SDK. This package references only Video, Infrastructure and `Microsoft.Extensions.Http`. Vertex uses a different resource/authentication protocol and is not selected by this adapter.

| Operation | Native wire | SDK entry |
| --- | --- | --- |
| Submit | `POST models/{actualModel}:predictLongRunning` | `SubmitAsync(VideoTaskRequest)` |
| Query once | `GET {originalOperationName}` | `GetAsync(string)` or `loki.Video.GetAsync(reference)` |
| Copy selected output | `GET {nativeVideoUri}` | `CopyContentToAsync(Uri, Stream)` on the configured adapter |

**Availability checked 2026-10-20:** Google has [announced shutdown](https://ai.google.dev/gemini-api/docs/deprecations/) of `veo-3.1-generate-preview`, `veo-3.1-fast-generate-preview` and `veo-3.1-lite-generate-preview` on **2026-10-22**. This adapter retains their currently available native protocol; it does not extend their availability or switch to another model/API. No default model, model catalog or automatic migration is supplied.

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.Google;

// Application-owned IHttpClientFactory; register this named client without
// retries, automatic redirects, or default credential headers.
// services.AddHttpClient("Asgard.Loki.Video.Google")
//     .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
//     { AllowAutoRedirect = false, UseCookies = false });
var target = new GoogleVideoAdapter(factory, new GoogleVideoOptions
{
    Id = "gemini-veo", Model = actualModel, ApiKey = apiKey
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
        ["instances"] = new JsonArray(new JsonObject { ["prompt"] = "A paper boat on a moonlit lake." }),
        ["parameters"] = new JsonObject { ["durationSeconds"] = 8, ["aspectRatio"] = "16:9" }
    }
}, cancellationToken);
// Persist submitted.Reference if required. Each query is explicit and runs once.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
if (current.Snapshot.State == VideoTaskState.Succeeded)
{
    var samples = current.Snapshot.Data.GetProperty("response")
        .GetProperty("generateVideoResponse").GetProperty("generatedSamples");
    var uri = new Uri(samples[0].GetProperty("video").GetProperty("uri").GetString()!);
    await target.CopyContentToAsync(uri, destinationStream, cancellationToken);
}
```

The default versioned root is `https://generativelanguage.googleapis.com/v1beta/`. Configure a different HTTPS root explicitly if needed. Actual model, root, API Key identity, client name, implementation, byte limits and redirect policy enter the complete target identity. Persisted task references reject changes before query IO. The original operation name `models/{actualModel}/operations/{id}` is retained and must match the configured model and requested operation.

`Parameters` is the original REST body: nonempty `instances` object array, optional `parameters` object, and all other native fields. The actual model belongs in the URL; root `model` is reserved. Text, initial/last frames, reference images, video inputs, original media data and unknown parameters are snapshotted without conversion. Compatibility of a particular field/value with the selected model remains an upstream decision. The SDK neither fetches input URLs nor rewrites their media objects.

The complete JSON operation remains in `VideoTaskSnapshot.Data`, including metadata, multiple `generatedSamples`, filtering reasons, native errors and unknown fields. Absent `done` and `done=false` mean running. `done=true` with valid samples means success; an operation error or an explicit all-filtered response maps to failure while retaining the original envelope. A partially filtered result with valid samples remains successful. Missing results, contradictory done/error/response fields, foreign operation names, malformed UTF-8/JSON and duplicate properties fail explicitly. `NativeStatus` contains the original boolean text or null for omitted `done`.

HTTP and unnamed root API errors throw `VideoUpstreamException` with original status, content type and defensive raw bytes. A named operation's native failure stays a task result. Submissions and queries are never retried or redirected by the SDK; configure the injected HTTP pipeline accordingly.

Content transfer is a separate explicit adapter call. Supply the selected original `video.uri`; no URI is invented from a task ID. Only the configured origin and root's `files/{id}:download` path, with no query or `?alt=media`, can receive the API Key. This validates the native download endpoint, not ownership of a file by an operation. The SDK follows at most three explicit HTTPS content redirects by default and sends **no API Key on any redirect**, including same-origin redirects. The application's named HTTP client must disable automatic redirects and have no default credential headers; custom handlers must honor this contract. Signed redirect query strings remain intact. No range/resume, file CRUD, URL refresh, polling or background transfer is provided.

Content must return HTTP 200 and `video/*` or `application/octet-stream`; JSON/error media is never written to the destination. Copy writes raw bytes at the caller's current stream position, without closing, seeking, flushing or committing that stream. Partial writes can remain on failure; the application owns commit/discard policy. Request, response and newly created client resources are disposed before successful return and final cancellation checks. The factory is borrowed.

Defaults: 64 MiB request JSON, 16 MiB response/error JSON, 512 MiB content, three content redirects (configurable 0–10). Infrastructure supplies bounded raw-byte read/copy only; native operation parsing, authentication, lifecycle and URI policy remain in this provider.

Offline xUnit v3 tests cover independent REST fixtures, complete input/output preservation, persistent target binding, operation identity, omitted done, native failure/filtering, content credentials/redirects, byte limits and final disposal/cancellation. **No live Veo generation, model availability or content download has been verified.**

References: [native prediction endpoint](https://ai.google.dev/api/models#method:-models.predictlongrunning), [Veo REST guide](https://ai.google.dev/gemini-api/docs/veo).
