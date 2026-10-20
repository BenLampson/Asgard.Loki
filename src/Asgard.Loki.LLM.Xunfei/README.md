# Asgard.Loki.LLM.Xunfei

Native Xunfei Spark WebSocket model calls with explicit targets and signed URL authentication. The package references only LLM; it does not host a gateway, execute tools, discover accounts or manage credentials. Native results keep their complete original message windows.

| Operation | Support |
| --- | --- |
| `loki.LLM.Spark.CompleteAsync` | One native exchange; complete ordered `SparkCompletion.Events` window |
| `loki.LLM.Spark.StreamAsync` | Original complete JSON messages; native terminal and normal-close validation |
| Native URL HMAC authentication | Once-per-call External principal credentials; explicit AppId/endpoint/domain |
| Search/plugin/usage and unknown fields | Original native JSON, every text entry and opaque content retained |
| HTTP/SSE, implicit Chat conversion, function execution | Unsupported |
| Account login, Key discovery/refresh, retry or failover | Unsupported |

## Initialization

Provide an application-owned `IXunfeiSparkCredentialSource` with a stable secret-free BindingIdentity. It returns immutable `XunfeiSparkCredentials` for the exact bound logical principal, containing API Key, API Secret and ExpiresOn. The principal remains stable during Key rotation. AppId belongs to immutable provider options and enters the provider binding identity. Login, provisioning and refresh remain application responsibilities. Default credential JSON/display excludes the principal and signing materials.

```csharp
// credentialSource and sessionStore are owned and supplied by the application.
var provider = new XunfeiSparkProvider(credentialSource, new() { AppId = appId });
var credential = LlmCredential.External("spark-account", "application-principal");
var target = new LlmTarget
{
    Id = "spark-native", ProviderId = provider.Id,
    BaseAddress = new Uri("https://spark-api.xf-yun.com/v1.1/chat"),
    UpstreamModel = "lite", CredentialId = credential.Id, Protocols = LlmProtocol.Spark
};
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    Providers = [provider], Credentials = [credential], SessionStore = sessionStore,
    Routes = [new() { Model = "public-spark", Targets = [target] }]
}).Build();
var request = new SparkRequest
{
    Model = "public-spark", Parameters = new()
    {
        ["payload"] = JsonSerializer.SerializeToElement(new
        { message = new { text = new[] { new { role = "user", content = "Hello" } } } })
    }
};
var window = await loki.LLM.Spark.CompleteAsync(request, new() { SessionId = "conversation" });
foreach (var item in window.Events) { /* item.Data is a full original native message. */ }
```

The core maps the public route only to `parameter.chat.domain`; the supplier adds its fixed `header.app_id`. The endpoint is the complete native address, including path. HTTP maps to WS and HTTPS to WSS with the same authority/path; nothing is appended or inferred from a model name. Query/fragment/user-info and escaped/non-ASCII paths are rejected before credentials. Use an ASCII authority and native path. Domains, versions, roles and service/model availability are caller-selected; no catalog, version mapping, automatic migration or role/history rewrite is provided.

`sessionStore` uses `IDistributedCache` plus an explicit first-binding coordinator; see the [core contract](../Asgard.Loki.LLM/README.md#native-spark-contract). A Session binds server, native domain, credential principal and provider configuration. Each call creates an independent socket and sends the caller's explicit history; a logical Session does not recover remote conversation history. API Key rotation for the same principal is allowed. Changed AppId, endpoint/domain, source/factory configuration or transmission policy cannot silently reuse an existing binding.

## Native fidelity and lifecycle

The body is validated and copied before credential waits. Only fixed AppId is inserted by the provider; root model/stream, domain mismatch, app_id overrides, incorrect native path casing, duplicate JSON and malformed original Unicode fail explicitly. Native message role/content strings are required and preserved without templates, defaults, clipping or system-role conversion. Request bytes are bounded after AppId insertion. Similar names inside unknown business objects remain untouched.

Current [URL authentication](https://www.xfyun.cn/doc/spark/general_url_authentication.html) uses HMAC-SHA256 over the configured host, RFC1123 UTC date and GET path. The authorization origin uses `api_key`, then Base64 and separate RFC3986 query encoding. Keys are opaque; `|` is never parsed. The default factory performs one standard ClientWebSocket handshake and sanitizes connection failure text to avoid exposing the signed URI. Application-supplied factories must honor cancellation, return a new owned socket for the exact URI, avoid retries/redirects and never log the signed URL.

WebSocket text fragments are assembled only to one original JSON message. Native code/sid/status and choice status/sequence are checked without converting to another protocol. Native sid stays stable within a call. Sequences must increase but may have gaps. Plugin prefixes can precede text, and header/text statuses are separate. Text `index` is retained as a reserved field; it is never used to collapse entries or construct candidate associations. Every original text/plugin entry, usage value, reasoning extension, null/false/exact number and unknown field remains in Data. Native plugin content stays opaque and is not fetched, parsed into a tool loop or executed. Model-specific parameter/role/tool availability is not established by passthrough.

From the first text/global finish signal, all original messages are buffered. Both native text and global completion are required. The SDK then sends CloseOutput with normal status and explicitly reads the peer's normal close acknowledgement, preserving native errors and rejecting extra JSON. It does not use CloseAsync to consume unobserved application messages or wait for an unsolicited server EOF. The entire terminal window is withheld through successful close, socket disposal, source/factory/principal/expiry and final cancellation checks. Late data/errors, early/abnormal/missing close, timeout, disposal failure or cancellation cannot release that buffer. Previously consumed preterminal messages remain partial output. Early consumer disposal closes the owned socket.

Complete calls preserve the whole ordered native window rather than merging the first text or totaling usage. `XunfeiSparkUpstreamException` retains a nonzero native code, original bytes and NativeData without inventing an HTTP status. No native sid is registered as a Responses continuation ID. Borrowed source/factory/clock/cache/coordinator objects are never disposed by this supplier; sockets and per-call timeout sources are owned.

## Limits and validation

Defaults: request 1 MiB, each message 1 MiB, total received message payload 32 MiB, 8,192 messages, 8,192 fragments per message and 128 terminal messages. Limits fail rather than truncate. Handshake timeout is 30 seconds and close-confirmation timeout is 10 seconds; all policies participate in provider identity and can be explicitly configured. The caller's cancellation token covers credential acquisition and the complete operation; there is no background receive, reconnect, retry or credential refresh. Timeouts use the borrowed TimeProvider.

The [official native reference](https://www.xfyun.cn/doc/spark/Web.html), checked 2026-10-20, documents available WebSocket endpoints and a Max-package retirement/backend upgrade on 2026-03-10. This is not a whole-protocol shutdown. The SDK supplies no model default or retired-model migration. The example uses an explicitly chosen Lite endpoint/domain; select a currently available matching profile for the application. The current WebSocket reference directs FunctionCall use to HTTP, so this package does not claim that feature through this protocol.

115 offline xUnit v3 cases use independent message/signature fixtures, fake sockets, a manually controlled clock and memory cache. They compare all eight captured signed URIs and complete native input/message windows, including the separately transcribed literal JSON messages. They inject state/fragment/encoding/limit/authentication/closure/cancellation/disposal failures. No real Spark credentials, handshake, model quality, model-specific feature acceptance, server availability or live integration has been validated.
