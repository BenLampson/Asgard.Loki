# Asgard.Loki.LLM.Dify

Native app-scoped `chat-messages` calls through a supplier-owned fixed-service client. References only LLM, capability-independent Infrastructure and standard HTTP. The app selects its underlying model remotely; this client introduces no fake model route, LlmProtocol, model Session or shared business coordinator.

## Support matrix

| Operation | Support |
| --- | --- |
| `DifyChatClient.SendAsync` | One native blocking POST for explicit Chatbot/Chatflow profiles; complete original message or Chatflow pause JSON |
| `StreamAsync` | One native SSE POST for Chatbot/Agent/Chatflow; original data, separate transport identity, complete terminal/TTS tail |
| Agent blocking | Rejected before HTTP; native Agent apps require streaming |
| Native inputs/query/user, conversation/file references and optional parameters | Preserved without defaults, upload, media fetching, history flattening or thinking-markup transformations |
| Target/authentication | Explicit complete versioned root, app API Key, app profile, target/credential identity/version and named borrowed factory |
| Workflow hosting/resume/polling, file management, app discovery/administration | Not implemented |
| Live app/model/parameter/output validation | Not performed; offline fixtures and fake HTTP only |

## Initialization

```csharp
using Asgard.Loki.LLM.Dify;
using System.Text.Json;

var chat = new DifyChatClient(httpClientFactory, new DifyChatOptions
{
    BaseAddress = new Uri("https://api.dify.ai/v1/"), // Explicit cloud, self-hosted or proxy root.
    Profile = DifyChatProfile.Chatbot,
    TargetId = "my-published-app",
    CredentialId = "my-app-key"
}, appApiKey);
var result = await chat.SendAsync(new DifyChatRequest
{
    User = applicationUserId,
    Query = "Hello",
    Inputs = JsonSerializer.SerializeToElement(new { }),
    Parameters = new()
    {
        ["auto_generate_name"] = JsonSerializer.SerializeToElement(false)
    }
}, cancellationToken);
// Keep the original conversation_id with the same application user for a later explicit request.
```

User, Query and object Inputs are explicit. Parameters carries native optional fields, including conversation_id, files and Chatflow workflow_id. Omitted, empty and null conversation values retain their wire distinction; a non-empty requested conversation must match the response. The caller owns user/conversation scope; no local session cache, guessed user or remote discovery is involved. File URL/upload_file_id values are references only. The SDK does not upload, decode or fetch them.

Only response_mode belongs to the method. Reserved case aliases, Undefined, invalid original Unicode and duplicate JSON properties fail before factory creation. All body data is snapshotted before transport awaits/factory creation. Unknown fields, exact numbers, zero, false, null, usage/retrieval/reasoning metadata and original answer markup remain native. Response id, message_id, task_id, conversation_id and workflow_run_id are independent domains. A Chatflow workflow_paused result stays paused without a fabricated success or automatic resume.

Defaults: 1 MiB request and 32 MiB complete/error body. Success requires strict UTF-8 JSON, valid declared lengths, matching method/complete URI, normal EOF, owned-resource closure and final cancellation checks. HTTP and native HTTP200 errors preserve raw bytes, HTTP status and content type in LlmUpstreamException. No automatic retry, target switch or follow-up occurs. Per-call clients/messages/responses/body streams are owned; the supplied factory remains borrowed. Configure the named HTTP transport to disable automatic redirects/retries and choose suitable timeouts.

## Native streams

```csharp
await foreach (var frame in chat.StreamAsync(request, cancellationToken))
{
    // frame.Data and frame.Json retain each native record; the app chooses rendering.
    ConsumeNativeEvent(frame);
}
```

Use an explicit Agent profile for Agent/Legacy Agent applications. Root JSON event selects native lifecycle; SSE EventName/EventId/RetryMilliseconds remain separate transport data. Bare ping without data is a control frame and produces no invented model message. Message/agent_message/thought/file/replacement/node/run/audio/future records keep original order, values and opaque content. An Agent's final message may repeat the whole answer: the SDK does not concatenate it with preceding chunks. File/audio values are not fetched or decoded.

Chatbot/Agent use native message_end. A started Chatflow needs message_end followed by successful workflow_finished; failed/stopped/paused outcomes retain their native semantics, including stopped workflow_finished before message_end. Native input-moderation/annotation direct answers can end with message_end without starting a workflow. That exception applies only when no workflow start, node event or known run identity was observed; incomplete observed runs fail. A node_finished or nested business event/type does not complete the call. Root event/response IDs, thought/file/node execution IDs, message IDs, task IDs, workflow IDs and run IDs stay in their own domains. Known identity drift, duplicate/lost terminals and late generation fail instead of rebinding.

The SDK holds every record from the first ending stage through original TTS/metadata tails and normal strict bounded EOF, exact declared lengths, method/target/resource-close/final-cancellation checks. Native error after an ending stage still fails with its original body before any held terminal is delivered. There is no synthetic DONE, stop, answer, usage total, retry, tool execution or reconnect. IsTerminal identifies a native end, including failure/stop/pause; it is not a success flag. Defaults: 64 MiB entire raw SSE, including controls, and 128 held tail records.

Offline validation: 127 passing xUnit v3 cases cover independent complete JSON and three-profile SSE windows, precise/unknown values and separate IDs, direct replies/paused/stopped/failed outcomes, strict inputs/errors, snapshots, byte/tail/length limits, EOF/disposal/cancellation and target/authentication guards. No real credential/app/model request has been made.

Primary contracts: [Send Chat Message](https://docs.dify.ai/en/api-reference/chat-messages/send-chat-message) and [Streaming](https://docs.dify.ai/en/api-reference/guides/streaming).
