# Asgard.Loki.LLM.Anthropic

Native Claude Messages provider for the Asgard.Loki .NET SDK.

| API / feature | Support |
| --- | --- |
| `POST /v1/messages` | Complete JSON and SSE |
| Tools, image/document blocks, thinking/signatures, cache controls, future JSON fields | Preserved in their native shape |
| Unknown SSE events, event IDs and retry hints | Preserved; hints do not cause retries |
| HTTP errors / interrupted streams | Original HTTP status/body; missing `message_stop` or error terminal fails |
| Bedrock / Vertex AI authentication, token-count and batch APIs | Not implemented |

```csharp
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Anthropic;

var provider = new AnthropicLlmProvider(httpFactory);
// Register provider in LlmOptions.Providers and configure an ordinary LlmTarget:
var target = new LlmTarget
{
    Id = "claude-primary", ProviderId = provider.Id,
    BaseAddress = new Uri("https://api.anthropic.com/v1/"),
    UpstreamModel = claudeModel, CredentialId = "claude-key",
    Protocols = LlmProtocol.ClaudeMessages
};
// Credentials = [new LlmCredential("claude-key", claudeApiKey)]
// SessionStore and the HTTP factory remain caller-owned, as for OpenAI/Ark.
var result = await loki.LLM.ClaudeMessages.CreateAsync(new ClaudeMessageRequest
{
    Model = "assistant", MaxTokens = 512,
    Messages = [new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "Hello" }]
}, new LlmCallOptions { SessionId = "claude-conversation" }, cancellationToken);
```

The target must be included in a route whose public `Model` is `assistant`. The model name and output limit are explicit caller choices. Authentication uses request-local `x-api-key` and `anthropic-version` headers; the default version is `2023-06-01`. Select another version with the constructor. Configure optional `anthropic-beta` headers on the named HTTP client `Asgard.Loki.LLM.Anthropic`. Changing version/client identity invalidates old Session bindings; change `LlmTarget.Version` when external HTTP defaults change semantics.

The provider references only `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not convert Chat/Responses or execute tools. Optional explicit conversion is supplied separately by `Asgard.Loki.LLM.Conversion`. Disable generation POST retries and hedging in the supplied HTTP factory.

Verification uses xUnit v3 with substitute HTTP handlers, fragmented SSE, `MemoryDistributedCache` and a test coordinator. No Anthropic live account/model verification has been performed. See [Messages API](https://platform.claude.com/docs/en/api/messages/create) and [streaming](https://platform.claude.com/docs/en/build-with-claude/streaming).
