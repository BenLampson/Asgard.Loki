# Asgard.Loki.Video.GoogleVertex

Native resource-scoped **Vertex Veo** operations for the pure .NET SDK. References only Video, Infrastructure and `Microsoft.Extensions.Http`. Authentication, resources, result parsing and native state stay in this package; no LLM or Gemini Developer API provider is required.

| Operation | Native wire | SDK entry |
| --- | --- | --- |
| Submit | `POST {fullModelResource}:predictLongRunning` | `SubmitAsync(VideoTaskRequest)` |
| Query once | `POST {fullModelResource}:fetchPredictOperation` with original `operationName` | `GetAsync(string)` or `loki.Video.GetAsync(reference)` |
| Obtain result | Complete native `response.videos` in the query result | Inspect GCS references or inline bytes in `Snapshot.Data` |

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki;
using Asgard.Loki.Video;
using Asgard.Loki.Video.GoogleVertex;

// factory and tokenSource belong to the application.
// Register the named client without automatic retries or redirects.
var target = new GoogleVertexVideoAdapter(factory, tokenSource,
    new GoogleVertexVideoOptions
    {
        Id = "vertex-veo",
        BaseAddress = new Uri("https://us-central1-aiplatform.googleapis.com/v1/"),
        ModelResource = $"projects/{project}/locations/us-central1/publishers/google/models/{actualModel}",
        PrincipalIdentity = principalIdentity
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
        ["instances"] = new JsonArray(new JsonObject { ["prompt"] = "A paper bird over a moonlit lake." }),
        ["parameters"] = new JsonObject { ["durationSeconds"] = 8, ["sampleCount"] = 1 }
    }
}, cancellationToken);
// Persist the complete reference if needed. Each call below queries once.
var current = await loki.Video.GetAsync(submitted.Reference, cancellationToken);
if (current.Snapshot.State == VideoTaskState.Succeeded)
{
    var originalVideos = current.Snapshot.Data.GetProperty("response").GetProperty("videos");
    // Application chooses a sample and handles its gcsUri or bytesBase64Encoded.
}
```

Configure the versioned HTTPS root and full `projects/{project}/locations/{location}/publishers/google/models/{model}` resource explicitly. There is no default model, automatic region/host selection, endpoint discovery or automatic migration. The official [Veo 3.1 model card](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/veo/3-1-generate) documents currently available stable models; account access, regional support and field compatibility remain upstream constraints. This adapter does not select Gemini API previews.

Implement `IGoogleVertexVideoTokenSource` in the application. Its `BindingIdentity` describes stable authentication configuration, not an access token. `GetTokenAsync(principalIdentity, cancellationToken)` returns one `GoogleVertexVideoAccessToken` containing the same asserted principal, raw token and expiry. The source owns login, scope selection, token refresh and the truth of that principal assertion. The SDK does not inspect JWTs, read service-account files, discover ADC, call a metadata server or acquire tokens on its own.

Each native operation asks the source once and sends one Bearer-authenticated model request. Wrong principals, null/expired tokens and changing source identities fail explicitly. Expiry is checked again immediately before HTTP send, including after client creation. The source may return a refreshed token for the same stable principal on a later explicit call; token rotation does not change the task target. No automatic retry, refresh loop or identity switch occurs. Tokens are excluded from default JSON, display strings and persisted task references.

`QuotaProjectId` sets only the explicit native `x-goog-user-project` header. It is not inferred from the model resource or principal. Unbound default API Key/project headers are rejected. The complete target identity includes root, full model resource, stable principal, source identity, explicit quota project, client, implementation and byte policies. Persisted references reject configuration drift before token acquisition or query IO. Source configuration is also checked before/after authentication and after final resource disposal. See the [Video reference contract](../Asgard.Loki.Video/README.md).

`Parameters` is the original REST body: nonempty `instances` object array, optional `parameters` object and unknown fields. The actual model lives in the resource URL; root `model` is reserved. Native prompts, image bytes/GCS references, initial/last frames, reference images, settings and unknown values are copied before authentication waits, preserving case, null/false/zero and numeric precision. The SDK does not convert Gemini API `inlineData`, fetch input media, inject model settings or certify compatibility of every field with the configured model.

The complete native operation remains in `VideoTaskSnapshot.Data`: metadata, errors, filtering, every `videos` element, original GCS URI, inline base64 string and unknown members. It is not wrapped as `generateVideoResponse/generatedSamples`, decoded to a data URL or downloaded. Missing `done` and `done=false` mean running. `done=true` with valid native output carriers maps to success; a native operation error or explicit all-filtered result maps to failure. Partial filtering with valid outputs remains successful. `NativeStatus` is original boolean text or null for omitted `done`. Missing or contradictory terminals, foreign operations, malformed UTF-8/JSON and duplicate properties fail explicitly. Success describes native task completion, not independent validation of encoded video content.

HTTP and unnamed root API errors throw `VideoUpstreamException` with status, content type and original raw bytes. Named operation failures remain task results. Queries retain the original full operation name in the POST body; no gateway task ID is invented or decoded. There is no SDK polling, task cache, media downloader, GCS client, file CRUD, callback host or background workflow.

Defaults are 64 MiB request and 128 MiB response/error bytes; the response bound includes inline media. Increase explicit limits when required. Infrastructure supplies bounded byte reads only; native authentication, JSON, resources and state belong here. The application owns factory, token source and optional `TimeProvider`; SDK calls own and dispose newly created clients/messages/responses. A result is returned only after complete body reading, disposal, source-identity and final cancellation checks.

Offline xUnit v3 tests cover independent REST fixtures, native request/result fidelity, persisted target binding, omitted done, errors/filtering, inline media/GCS results, token rotation/expiry/principal drift, input snapshots, byte limits and final disposal/cancellation. **No real Vertex Veo generation, cloud authentication, storage access or account/model availability has been verified.**

References: [text generation and operation query](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/video/generate-videos-from-text), [image generation and inline output](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/video/generate-videos-from-an-image).
