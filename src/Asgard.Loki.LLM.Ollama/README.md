# Asgard.Loki.LLM.Ollama

Native Ollama **`POST /api/chat` and `/api/generate`** JSON and NDJSON provider. This package references only `Asgard.Loki.LLM` and standard HTTP dependencies. It does not install the separate Embedding provider, start a server, manage models, execute tools or run a conversation loop.

## Support matrix

| Operation | Status |
| --- | --- |
| `.LLM.OllamaChat.CompleteAsync` | Native JSON; explicit `stream:false` |
| `.LLM.OllamaChat.StreamAsync` | Native NDJSON records; explicit `stream:true`; also accepts a single JSON terminal for native load/unload calls |
| `.LLM.OllamaGenerate.GenerateAsync` / `StreamAsync` | Native prompt/context JSON/NDJSON; separate protocol and result/event shapes |
| Messages, images, thinking and tool history | Original native JSON, tool IDs, `tool_name`, `tool_call_id` and object arguments retained; no ID synthesis or aggregation |
| `think`, `format`, `options`, `keep_alive`, future fields | Native extension parameters, including false/zero/null and complete JSON Schema; model acceptance remains upstream-owned |
| Authentication | Explicit `LlmCredential.Anonymous(id)` or selected API Key Bearer identity |
| Session | Complete server/model/credential/protocol/provider/configuration binding with explicit distributed first-binding coordination |
| Model management, cloud token refresh | Not implemented |
| Conversion to/from other LLM protocols | Not implemented; the separate conversion package still supports its documented four-protocol matrix |
| Real Ollama model/account/media validation | Not performed; offline fixtures only |

## Initialization

Supply an `IHttpClientFactory`, a shared `ILlmSessionStore` and explicit targets. `DistributedCacheLlmSessionStore` requires `IDistributedCache` plus an application-provided `ILlmSessionLock`; ordinary cache Get/Set is not cross-instance coordination. See the [root initialization](../../README.md).

```csharp
using Asgard.Loki;
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Ollama;
using System.Text.Json;
using System.Text.Json.Nodes;

await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    SessionStore = sessionStore,
    Providers = [new OllamaLlmProvider(httpClientFactory)],
    Credentials = [LlmCredential.Anonymous("local-instance")],
    Routes = [new LlmRoute
    {
        Model = "local-chat",
        Targets = [new LlmTarget
        {
            Id = "local-ollama", ProviderId = "ollama",
            BaseAddress = new Uri("http://localhost:11434/api/"),
            UpstreamModel = actualInstalledModel, CredentialId = "local-instance",
            Protocols = LlmProtocol.OllamaChat
        }]
    }]
}).Build();

var request = new OllamaChatRequest
{
    Model = "local-chat",
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" }),
    Parameters = new Dictionary<string, JsonElement>
    {
        ["think"] = JsonSerializer.SerializeToElement(true),
        ["options"] = JsonSerializer.SerializeToElement(new { temperature = 0 })
    }
};
var result = await loki.LLM.OllamaChat.CompleteAsync(request,
    new LlmCallOptions { SessionId = "conversation-1" }, cancellationToken);
Console.WriteLine(result.Message);

await foreach (var record in loki.LLM.OllamaChat.StreamAsync(request,
    new LlmCallOptions { SessionId = "conversation-1" }, cancellationToken))
    Console.WriteLine(record.Data); // Original native record, including errors.
```

The root is explicit and includes `/api/`; the provider appends `chat`, preserving any proxy prefix. It does not infer endpoints from the host/model name. For a caller-configured Bearer endpoint use `new LlmCredential(id, apiKey)` and bind that ID on the target. Missing keys never select anonymous mode. An anonymous target rejects a factory client's default `Authorization` before HTTP dispatch rather than borrowing its credential. Caller-supplied handlers remain responsible for their own header policy. The SDK adds no token acquisition or refresh.

## Native generate

Configure a target with `Protocols = LlmProtocol.OllamaGenerate` (or explicitly combine both native flags). The provider appends `generate` to the configured API root. Call `.LLM.OllamaGenerate.GenerateAsync` / `StreamAsync`; these use `OllamaGenerateResponse` / `OllamaGenerateEvent` and retain `response`, root `thinking`, `context`, logprobs, metrics and unknown fields. No message wrapper is added. A Session cannot switch between Chat and generate even when the configured target supports both.

```csharp
var generate = new OllamaGenerateRequest
{
    Model = "local-chat",
    Parameters = new Dictionary<string, JsonElement>
    {
        ["prompt"] = JsonSerializer.SerializeToElement("Write a short greeting"),
        ["raw"] = JsonSerializer.SerializeToElement(false)
    }
};
var completion = await loki.LLM.OllamaGenerate.GenerateAsync(generate,
    new LlmCallOptions { SessionId = "completion-1" }, cancellationToken);
Console.WriteLine(completion.Response);
// Return context only when the application explicitly wants to replay it:
if (completion.Data.TryGetProperty("context", out var context))
    generate.Parameters["context"] = context.Clone();
```

`OllamaGenerateRequest` names only the routed model; native `prompt`, `suffix`, `system`, `template`, `context`, images and settings use extension JSON. This preserves omission, empty strings and explicit null without injecting prompt/history defaults. The SDK neither templates prompts nor automatically persists/replays returned context. Local Session affinity does not imply a server-side conversational history. The provider identity now includes `generate-v1`; bindings made by the preceding Chat-only implementation must use a fresh Session or caller-managed expiration rather than silently resuming under a changed implementation.

## Fidelity and lifecycle

Only the actual model and method-selected stream flag are overwritten. Messages and extension JSON are snapshotted before awaited Session resolution; streams snapshot when enumeration begins. Responses retain native model identity, timestamps, tool arguments, unknown fields, exact numbers, counters and literal `done_reason`. There is no OpenAI envelope, synthesized usage, parsed image, downloaded media or model-specific rewriting. The SDK does not validate the quality of generated content or the model's adherence to a schema.

NDJSON accepts LF/CRLF, blank separator lines, arbitrary UTF-8 fragmentation and a complete final record without a trailing newline. Events have no fabricated SSE name/ID/retry values. Unknown object records remain visible but cannot complete a stream. Ordinary Chat records require a boolean `done`, native model and message; generate records require their own native response string; model identity cannot change during a stream. Missing terminal records, late/duplicate records, duplicate JSON properties, invalid UTF-8/escaped Unicode, malformed JSON, declared-length mismatch and read/disposal failures fail explicitly. No successful terminal is invented at EOF.

Native `done:true` and error records are held until EOF and response/client disposal succeed. Partial records remain incremental. HTTP failures raise `LlmUpstreamException` with original status/body; a complete HTTP-200 JSON error also raises it. An HTTP-200 stream error remains a native terminal event (`IsError`), including original error data; callers must check it rather than treating every terminal as successful generation. Cancellation and early consumer disposal release request-owned resources. Borrowed factories, stores and coordinators stay caller-owned. There is no automatic retry, reconnect or failover.

`OllamaLlmOptions.MaximumStreamRecordBytes` defaults to 1 MiB (original UTF-8 bytes excluding LF, including CR and whitespace); `MaximumResponseBytes` defaults to 32 MiB for complete JSON and HTTP error bodies. Oversize data fails without trimming. A single JSON stream terminal uses the stream-record limit. Limits and HTTP client name participate in implementation fingerprints, so changing them invalidates old Sessions instead of silently resuming under different settings. These are SDK resource limits, not provider quotas.

## Verification

167 deterministic xUnit v3 cases (97 Chat, 62 generate and eight shared public-snapshot encoding cases) cover independent full request/result/event fixtures, native tool/history association, exact numbers, reasoning/schema settings, mode/identity binding, pre-await snapshots, default authorization, framing and failure/ownership boundaries. Generate additionally verifies prompt omission/null/empty distinctions, explicit-only context replay and independent protocol/record validation. They use substitute HTTP handlers, byte-fragmented streams and `MemoryDistributedCache`; no live server, Key, Redis or model is required.

Primary contract references: [Chat](https://docs.ollama.com/api/chat), [generate](https://docs.ollama.com/api/generate), [streaming](https://docs.ollama.com/api/streaming), [errors](https://docs.ollama.com/api/errors), [authentication](https://docs.ollama.com/api/authentication). Independent fixtures were authored against these contracts and the native [v0.40.6 types](https://github.com/ollama/ollama/blob/v0.40.6/api/types.go); they are not copied mapper output. This checkpoint does not claim full New API parity or live model acceptance.

The public LLM clients now validate raw native DOM/extension Unicode before snapshots and Session work. Eight additional Chat/generate complete/stream cases verify malformed extension keys and escaped strings fail before cache/HTTP. Direct provider Unicode handling and well-formed native data remain unchanged; see the [LLM snapshot contract](../Asgard.Loki.LLM/README.md#native-json-snapshot-encoding).
