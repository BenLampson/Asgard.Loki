# Asgard.Loki.Video.XAI

Native xAI video operations for the pure .NET SDK. This provider references Video, Infrastructure and `Microsoft.Extensions.Http`; native authentication, status, moderation and result parsing stay here.

| Explicit operation | Submit | Query once |
| --- | --- | --- |
| `XaiVideoOperation.Generate` | `POST v1/videos/generations` | `GET v1/videos/{request_id}` |
| `XaiVideoOperation.Edit` | `POST v1/videos/edits` | Same native query |
| `XaiVideoOperation.Extend` | `POST v1/videos/extensions` | Same native query |

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.XAI;

// factory, actualModel and apiKey belong to the application.
var target = new XaiVideoAdapter(factory, new XaiVideoOptions
{
    Id = "xai-generation", Model = actualModel, ApiKey = apiKey,
    Operation = XaiVideoOperation.Generate
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
        ["duration"] = 5, ["resolution"] = "720p", ["generate_audio"] = false
    }
}, cancellationToken);
// Persist submitted.Reference if needed. Each query runs once.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
if (current.Snapshot.HasJsonData)
{
    var originalEnvelope = current.Snapshot.Data;
}
var originalHttpResponse = current.Snapshot.HttpResponse;
```

Native generation accepts original text, `image`, `reference_images`, `reference_audios`, `last_frame` and `keyframes` JSON, including frame order and timestamps. Editing and extension use the original `video` object and their own settings. Select the operation explicitly: neither model names nor media fields switch endpoints. Parameters are deeply copied before the first wait; only reserved root `model` is supplied by the configured target. Unknown members, nested objects, signed links, false/null/zero, omitted prompt and numeric precision remain intact. The SDK does not fill duration/resolution defaults, normalize aspect ratios, rewrite media or run a model catalog. Actual model/operation/media compatibility and account availability remain upstream constraints. See official [REST contracts](https://docs.x.ai/developers/rest-api-reference/inference/videos), [generation/status/error guide](https://docs.x.ai/developers/model-capabilities/video/generation), [reference/frame inputs](https://docs.x.ai/developers/model-capabilities/video/reference-to-video) and [extension guide](https://docs.x.ai/developers/model-capabilities/video/extension).

The default unversioned root is `https://api.x.ai/`; set another HTTPS root/region or prefix explicitly. Native Bearer authentication sends the original API Key without prefix detection or environment discovery. The complete identity covers root, actual model, selected operation, credential digest, named client, implementation and byte policies. Persisted [Video references](../Asgard.Loki.Video/README.md) reject drift before query IO, including an operation change despite the shared query path. The HTTP factory remains borrowed; configure its named client without automatic retry, hedging or redirects. No login or token acquisition service is added.

Submission requires a nonempty original string `request_id`. Queries escape the requested ID as one path segment. Native query JSON can omit that ID; `Snapshot.TaskId` then retains SDK call context without inserting a field into `Data`. An explicitly returned `request_id` must match exactly. No numeric conversion or trimming changes original identifiers.

Known `pending`, `done`, `failed` and `expired` statuses map to running, succeeded, failed and expired. Future statuses remain unknown and nonterminal even at progress 100. A successful `done` requires its original `video` object and nonempty URL; a present `respect_moderation` must be boolean. `respect_moderation=false` maps to terminal failure while preserving native `status=done` and the complete original envelope, including an omitted URL. Failed tasks remain failed regardless of original usage amounts. The SDK does not fabricate an error code/text or convert native monetary usage into tokens. All usage, moderation flags, links, duration, actual returned model and unknown fields stay in `Data`.

Every returned snapshot also retains copied original HTTP status/body/content type in `HttpResponse`. An exactly zero-byte HTTP 202 query returns running with `HasJsonData=false`, `Data.ValueKind=Undefined` and no invented native status or JSON object. Nonempty JSON must be a valid original object with a query status. Empty submissions, empty responses with other statuses, JSON null/arrays, malformed encoding, duplicate fields, wrong IDs, incomplete results and HTTP 202 terminal contradictions fail explicitly. HTTP failures and synchronous native error envelopes, including HTTP 200 errors, retain raw bytes/status/type in `VideoUpstreamException`; native failed-task errors remain original task data.

Request JSON defaults to a 64 MiB UTF-8 limit and raw response/error bytes to 16 MiB; these configurable transport bounds do not guarantee upstream model acceptance. Complete reading, declared-length agreement, transport disposal and final cancellation precede result delivery. Per-call messages, responses and created clients are disposed; the factory remains application-owned. No retry, failover, background polling, callback receiver, media fetch/download, credential forwarding to output URLs, URL refresh, storage/file management or billing service is provided. Native file/media identifiers are only inference inputs/outputs.

Validation is offline only: independent original JSON fixtures and substituted HTTP handlers cover generation variants, editing/extension, complete fields, persistent references, moderation, failure usage, absent JSON and resource boundaries. The empty-202 case is an offline transport test, not live provider evidence. No real xAI account, model, media processing, output content or URL retrieval has been verified.
