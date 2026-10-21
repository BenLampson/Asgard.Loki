# Asgard.Loki.Video.Kling

Native Kling video operations for the pure .NET SDK. This provider references Video, Infrastructure and `Microsoft.Extensions.Http`; authentication, native task states and result validation stay in this package.

| Explicit operation | Submit | Query once | Result |
| --- | --- | --- | --- |
| `KlingVideoOperation.TextToVideo` | `POST v1/videos/text2video` | `GET v1/videos/text2video/{task_id}` | Complete original task envelope and video list |
| `KlingVideoOperation.ImageToVideo` | `POST v1/videos/image2video` | `GET v1/videos/image2video/{task_id}` | Complete original task envelope and video list |

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.Kling;

// factory, actualModel and apiKey belong to the application.
var target = new KlingVideoAdapter(factory, new KlingVideoOptions
{
    Id = "kling-text", Model = actualModel,
    Operation = KlingVideoOperation.TextToVideo,
    Authentication = KlingVideoAuthentication.ApiKey, ApiKey = apiKey
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
        ["duration"] = "5", ["mode"] = "pro", ["sound"] = "off"
    }
}, cancellationToken);
// Persist submitted.Reference if needed. Each explicit query runs once.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
var originalEnvelope = current.Snapshot.Data;
```

Choose `ImageToVideo` explicitly for native `image`/`image_tail` input. Pass the original URL or raw Base64 expected by that endpoint; the SDK neither fetches inputs nor rewrites a data URI. Native duration remains a string. Unknown fields, arrays, media roles, callback URL, external task ID, watermark settings, null/false/zero and numeric precision remain intact. Parameters are copied before the first wait. Target configuration supplies only `model_name`; both `model` and `model_name` are reserved in request parameters. There are no model, duration or resolution defaults. A model name or media field never switches the chosen operation.

Authentication is explicit. `ApiKey` sends the original key as Bearer without prefix detection. For the legacy design's AK/SK authentication, set `Authentication = KlingVideoAuthentication.AccessKeyJwt`, `AccessKeyId` and `SecretAccessKey`, leaving `ApiKey` empty. Each explicit request creates one HS256 JWT with `iss`, `exp = + 1800` and `nbf = - 5`, using unpadded Base64url and the raw UTF-8 secret. The provider rechecks its time window after creating the HTTP client and fails without sending if the token is no longer valid; it does not refresh or retry. A borrowed optional `TimeProvider` supports application-controlled time. The default unversioned root is `https://api-singapore.klingai.com/`; configure another HTTPS root/region or prefix explicitly. Account permissions, regional availability and the selected model's supported fields remain upstream constraints. See official [authentication](https://kling.ai/document-api/api/get-started/authentication) and [native image-to-video documentation](https://kling.ai/document-api/api/video/2-5-turbo/image-to-video/legacy).

The complete target identity covers root, actual model, operation, authentication mode, credential digest, HTTP client, implementation and byte limits. Persisted [Video references](../Asgard.Loki.Video/README.md) reject target drift before query IO. Rotating per-request JWT timestamps do not change that identity. Query uses the original task ID as one escaped path segment and rejects another task's response. Custom external-ID lookup, task listing, newer route designs and other Kling operations are not implemented by these two endpoints.

Full native JSON remains in `VideoTaskSnapshot.Data`, including request/task IDs, timestamps, task errors, video IDs, all URLs/durations, watermark metadata, deduction fields and unknown members. Deduction amounts remain native values and are not converted into token counts. Native `submitted`, `processing`, `succeed` and `failed` map to the corresponding SDK states; future statuses stay unknown and nonterminal. An acknowledgement without a status is submitted; queries require a status. Known success requires a nonempty native `task_result.videos` array with a nonempty URL on every item. This validates the result carrier, not the video content itself. HTTP errors and nonzero native `code`, including HTTP 200 errors, retain original bytes/status/content type in `VideoUpstreamException`; malformed JSON/envelopes or task mismatches fail explicitly.

Defaults bound request JSON to 64 MiB and raw response/error bytes to 16 MiB. Cancellation reaches transport and is checked after complete reading and disposal. The factory and clock remain borrowed; per-call messages, responses and created clients are disposed. Configure the named HTTP client without automatic retry, hedging or redirects. The SDK adds no retry, target switching, polling service, callback receiver, URL refresh or video downloader, and never forwards credentials to returned media URLs.

Validation is offline only: independent native JSON fixtures, a separate HS256 vector and substitute HTTP handlers cover both operations/authentication modes, complete fields, persistent binding, task correlation, states and resource boundaries. No real Kling account, model, media processing or output content has been verified.
