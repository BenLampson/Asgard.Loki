# Asgard.Loki.Audio.SunoAPI

Native music and lyrics task calls for an explicitly configured [Suno-API/Suno-API](https://github.com/Suno-API/Suno-API/tree/a63492c2ef24f06080c467b608b063503731dde5) compatible proxy. This is an unofficial proxy protocol, independent of other services with similar names. The package requires an application-provided HTTPS root and does not manage Suno accounts, cookies or a proxy service.

## Support matrix

| SDK operation | Native API | Behavior |
| --- | --- | --- |
| Submit `Operation = "MUSIC"` | `POST suno/submit/MUSIC` | Original native JSON, explicit fixed `mv`, continuation task/clip IDs and zero offsets |
| Submit `Operation = "LYRICS"` | `POST suno/submit/LYRICS` | Original lyric prompt/unknown fields; no music model required |
| `GetAsync` | `POST suno/fetch` with one ID | Explicit status query; missing task throws `SunoAudioTaskMissingException` with native response |
| `GetBatchAsync` | `POST suno/fetch` with IDs | One native request, mixed music/lyrics on the same complete target; omissions remain explicit |
| `GetResultAsync` | `GET suno/fetch/{id}` | Complete original result envelope, without an inferred lifecycle |

The author protocol's GET returns song/lyrics data without task status. Status queries therefore use the native POST interface; they never infer completion from an audio URL. A compatible GET task wrapper is preserved, with returned task/operation echoes checked. Only string `code: "success"` is a successful native acknowledgement; numeric 200 is not substituted. Submit acknowledgements retain an absent native status and map only to the SDK Submitted state.

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Audio;
using Asgard.Loki.Audio.SunoAPI;

// Application-owned factory: disable automatic retries, hedging and redirects.
var target = new SunoAudioAdapter(httpClientFactory, new()
{
    Id = "music-target",
    BaseAddress = proxyRoot, // Required HTTPS root, optionally with an application prefix.
    Authentication = SunoAudioAuthentication.Bearer,
    ApiKey = proxyApiKey,
    MusicModel = selectedNativeMusicVersion // Fixed original mv; no built-in version default.
});
await using var loki = new LokiClientBuilder().AddAudio(new()
{
    TaskRoutes = [new() { Model = "music", Targets = [target] }]
}).Build();
var submitted = await loki.Audio.Tasks.SubmitAsync(new()
{
    Model = "music", Operation = "MUSIC",
    Parameters = new()
    {
        ["prompt"] = "Original lyrics",
        ["title"] = "Original title",
        ["tags"] = "caller-selected tags",
        ["make_instrumental"] = false
    }
}, cancellationToken);
// Persist the complete Reference; the application controls when to query again.
var current = await loki.Audio.Tasks.GetAsync(submitted.Reference, cancellationToken);
var nativeResult = await loki.Audio.Tasks.GetResultAsync(submitted.Reference, cancellationToken);
```

`MusicModel = null` permits lyrics-only submission. MUSIC requires an explicit native version: a missing request `mv` is bound once to that version; a conflicting one fails before HTTP. No historical version, `make_instrumental`, prompt trimming/stringification or other generation defaults are inserted. Operation names are explicit uppercase MUSIC/LYRICS. Known native field types and case aliases are validated, while original nested/unknown fields, false/null/zero and precise large integers remain unchanged. Continuation `task_id`, `continue_clip_id` and `continue_at` stay original. The public `Model` route is not sent as a native model field.

Use `Authentication = SunoAudioAuthentication.Anonymous` and an empty ApiKey only for an explicitly anonymous application proxy. That mode clears a factory client's default Authorization header. The SDK does not discover a proxy, reuse account state or borrow another capability's credentials. Factory and targets remain application-owned; each call disposes its own request/response/client before success is returned. Keep factory pipelines free from implicit replay or credential-bearing redirect behavior.

## Results and limits

Task references bind root, native music model, authentication/credential identity, adapter and byte policies as one complete target; secrets are excluded from public references/default option JSON. Queries do not rotate targets. Known `SUCCESS`/`FAILURE` map to terminal states; NOT_START/SUBMITTED, QUEUED and IN_PROGRESS remain nonterminal. UNKNOWN and future or differently cased states remain Unknown/nonterminal. Success requires usable original music or nonempty lyrics, without contradictory native failures. Nested legacy task-data carriers retain their full structure and must match the original task ID. Failed/running windows keep partial data and native failure details.

Complete JSON preserves original tracks and IDs, audio/video/cover addresses, lyric CRLF, model/version fields, metadata, times, native counters and unknown fields. No track count is assumed; successful song IDs must be distinct. Signed URL spelling is unchanged. The SDK does not aggregate music usage, render HTML, synthesize text fallbacks, download media, invoke callbacks or run polling/background jobs. `GetResultAsync` retains native null/empty results without calling them successful tasks. Batch `Data` keeps native order while the core associates returned tasks in request order and reports `MissingReferences`; extra, duplicate or wrong-operation tasks fail.

MaximumRequestBytes defaults to 4 MiB and MaximumResponseBytes to 16 MiB. Invalid UTF-8/JSON, duplicate fields, malformed status/identity, invalid success media, length/read/disposal errors and cancellation fail explicitly. `SunoAudioException` preserves actual HTTP status, full original bytes/content type and an optional native error response, without exposing upstream text in its message. There is no automatic retry/failover, protocol conversion or dependency on LLM/Video.

## Verification

Independent offline fixtures and xUnit v3 tests use fake HTTP only. They cover MUSIC description/continuation and LYRICS, full mixed/partial/empty batches, lifecycle/association, native original results/errors, exact JSON values and resource/cancellation boundaries. No real proxy credentials, model/version acceptance, service availability, music quality or media retrieval have been verified. There is no live test configuration for this package.
