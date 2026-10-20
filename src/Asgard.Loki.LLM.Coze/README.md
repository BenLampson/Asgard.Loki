# Asgard.Loki.LLM.Coze

Explicit Coze v3 **bot chat** calls in a supplier-owned native client. Each method sends one HTTP request. This package references only LLM, capability-independent Infrastructure and standard HTTP; it does not implement a relay, bot administrator, workflow host, model selector or automatic polling loop.

## Support matrix

| Operation | Support |
| --- | --- |
| `CozeChatClient.CreateAsync` | One native non-streaming POST; full acknowledgement/state, not a synchronous model answer |
| `RetrieveAsync` | One native GET using a complete-target-bound Conversation/Chat reference |
| `GetMessagesAsync` | One native chat-scoped GET; all ordered native messages, including multiple answers/intermediates |
| Native history, tool/required_action content, reasoning, metadata and future fields | Original JSON; no execution, aggregation, default user or last-answer projection |
| Target/authentication | Explicit versioned API root, Bot ID, independent API token, credential identity/version and named factory |
| Bot/conversation administration, model discovery, create-and-poll, workflow/tool execution | Not implemented |
| Live account/Bot/setting/answer-quality validation | Not performed; offline fixtures only |

## Initialization

Coze selects a configured Bot, whose underlying model is not selected by this API. The native client labels BotId honestly and does not inject a fake model, LlmProtocol, model Session or Responses ownership. Existing model clients and their complete-target Session coordination remain unchanged. The application owns the factory and remote conversation IDs.

```csharp
using Asgard.Loki.LLM.Coze;
using System.Text.Json;

var chat = new CozeChatClient(httpClientFactory, new CozeChatOptions
{
    BaseAddress = new Uri("https://api.coze.cn/v3/"), // Explicit region/version/proxy root.
    BotId = publishedBotId,
    TargetId = "my-coze-bot",
    CredentialId = "my-coze-token"
}, apiToken);
var acknowledgement = await chat.CreateAsync(new CozeChatRequest
{
    UserId = applicationUserId,
    Parameters = new()
    {
        ["additional_messages"] = JsonSerializer.SerializeToElement(new[]
        {
            new { role = "user", content = "Hello", content_type = "text" }
        }),
        ["auto_save_history"] = JsonSerializer.SerializeToElement(true)
    }
}, cancellationToken);
// This is an acknowledgement. The application decides when to make later calls.
var status = await chat.RetrieveAsync(acknowledgement.Reference, cancellationToken);
// When appropriate, explicitly get this chat's complete native message array.
var messages = await chat.GetMessagesAsync(acknowledgement.Reference, cancellationToken);
```

`ConversationId` is optional on CozeChatRequest and belongs only in the native query. UserId must be explicit; whitespace/valid Unicode are preserved, not trimmed or replaced by a random user. Bot ID and method stream mode belong to the fixed client. Reserved aliases/Undefined/bad original Unicode fail before factory creation. Complete native body snapshots precede all transport awaits; no message role/content/type, history saving, card/variables/schema, usage or generation defaults are injected. Media/file references remain data and are not fetched.

`code=0` means the API accepted the operation. Create may return in_progress, failed, canceled, requires_action, completed or future native states; no state becomes a fabricated answer/stop. The original code/msg/data/detail and log/unknown fields stay intact. GetMessages returns every original function_call/tool_response/answer/follow_up record in order, with reasoning and opaque content unchanged. Message.id is independent of Chat.id; only chat_id/conversation_id/bot_id are association checks. No tool execution or implicit follow-up call occurs.

The native service's history-saving setting affects later retrieval. The SDK preserves omission/false and never forces auto_save_history=true. Explicit calls can therefore return native service errors when history is unavailable. No local cache or conversation lifecycle is introduced to mask that result.

CozeChatReference can be serialized and restored. Its target fingerprint binds exact root/Bot/credential identity and key/configuration/version/implementation/policies; ConversationId and ChatId remain native IDs. Wrong targets fail before HTTP, and native echoes cannot silently substitute identities. The reference contains no token or arbitrary request URL. It is SDK binding metadata, not an authorization token; upstream permissions remain authoritative. All native query IDs are independently escaped.

Default local limits are 1 MiB request and 32 MiB complete/error body. The immutable options also capture stream/tail/association policies in target identity. Oversize, malformed/duplicate JSON, invalid Unicode, wrong echo IDs, length/actual-URI/method drift, EOF/read/disposal faults and final cancellation fail without returning a successful acknowledgement. The factory stays borrowed; each call owns its messages/responses/clients/read streams. Selected Bearer overrides default Authorization; alternative default API-Key headers are refused. Disable named-client redirects/retries/hedging and change Version when changing identity-affecting named-client behavior.

LlmUpstreamException keeps actual HTTP status, original body bytes and content type, including HTTP200 native nonzero code or binary HTTP errors. No credential discovery/refresh, other-project key lookup, hidden login, billing, training, file management, media fetching, backend listener or local orchestration is added.

References: [create](https://docs.coze.cn/developer_guides_chat_v3), [retrieve](https://docs.coze.cn/developer_guides_retrieve_chat), [chat messages](https://docs.coze.cn/developer_guides_list_chat_messages), [official SDK](https://github.com/coze-dev/coze-py/blob/main/cozepy/chat/__init__.py). Region/root/Bot availability and actual native option acceptance must be validated by the application; fixtures do not establish live service/model quality.

Offline JSON evidence: 101 cases compare independent full native request/response windows and injected binding/transport faults. They do not verify live Bot/account access, option effects or model answer quality.
