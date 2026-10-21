# Asgard.Loki.LLM.Tencent

Native Tencent Hunyuan model calls using API 3.0 TC3 authentication. This supplier package references only LLM, Infrastructure and standard HTTP dependencies. It does not interpret pipe-delimited keys, require an AppID, discover credentials or select a protocol from a model/key name.

| Operation | Support |
| --- | --- |
| Native ChatCompletions complete JSON | Yes; Action `ChatCompletions`, Version `2023-09-01` |
| Native SSE | Yes; original flat PascalCase events, tool fragments, all-choice finishes and complete trailing metadata |
| OpenAI-compatible TokenHub | Use an explicitly configured OpenAI provider/root instead |
| Implicit conversion, retries, STS acquisition, token refresh | Unsupported |

## Initialization

Supply an application-owned `ITencentHunyuanCredentialSource`, a borrowed `IHttpClientFactory` and an explicit complete LLM target. The source exposes a stable, secret-free `BindingIdentity`, and returns `TencentHunyuanCredentials` for the exact `LlmCredential.External` logical principal. That principal is independent of rotating SecretId/SecretKey/temporary Token values. Login, STS and refresh remain application responsibilities. The SDK requests credentials once per operation; it never discovers environment credentials or refreshes/retries implicitly.

```csharp
// httpFactory, credentialSource and sessionStore are supplied by the application.
var provider = new TencentHunyuanProvider(httpFactory, credentialSource);
var credential = LlmCredential.External("hunyuan-account", "application-principal");
var target = new LlmTarget
{
    Id = "hunyuan-native", ProviderId = provider.Id,
    BaseAddress = new Uri("https://hunyuan.ai.tencentcloudapi.com/"),
    UpstreamModel = "your-available-native-model", CredentialId = credential.Id,
    Protocols = LlmProtocol.Hunyuan
};
await using var loki = new LokiClientBuilder().AddLLM(new LlmOptions
{
    Providers = [provider], Credentials = [credential], SessionStore = sessionStore,
    Routes = [new() { Model = "public-model", Targets = [target] }]
}).Build();
var request = new HunyuanRequest
{
    Model = "public-model", Parameters = new()
    {
        ["Messages"] = JsonSerializer.SerializeToElement(new[] { new { Role = "user", Content = "Hello" } })
    }
};
var result = await loki.LLM.Hunyuan.CompleteAsync(request, new() { SessionId = "conversation" });
// result.Data retains the complete native Response envelope, including Response.Id.
```

`sessionStore` uses `IDistributedCache` and an explicit first-binding coordinator as described in the [LLM contract](../Asgard.Loki.LLM/README.md). Sessions retain server, actual model, credential principal and provider implementation. A changed source configuration/principal, expired credentials or target drift fails instead of changing targets. The factory/source/clock/store remain borrowed; per-call HTTP resources belong to the SDK. Credentials omit all identity/signing material from default JSON and display.

## Native fidelity and limits

Requests retain native PascalCase Messages, tool associations and schema/argument strings, reasoning/search options, multimodal references, null/false/exact numbers and unknown fields. Only the core maps public Model to the fixed actual model and owns Stream. The provider requires matching native Model/Stream and nonempty Messages with explicit Role; transport/authentication fields cannot be injected in the JSON. No message/tool history or content is generated, fetched, normalized or executed. Duplicate JSON properties and malformed original Unicode fail explicitly.

Complete results retain the entire Response envelope, all choices and native finish reasons, disclaimer Note, usage, reasoning, search data and future fields. Every choice requires a nonempty native finish reason and Message object. Native cloud Error/string Code and ErrorMsg/numeric Code remain distinct; native and HTTP errors retain raw bytes, status and content type in `LlmUpstreamException`. No success or converted ID is synthesized. The inherited lowercase Id accessor does not project Response.Id.

TC3 signs the exact final UTF-8 body with `application/json`, Host and lowercase action. The scope uses UTC date and service `hunyuan`; the API 3.0 canonical URI is fixed `/`, including when the physical endpoint has a proxy prefix. Optional `TencentHunyuanOptions.SigningHost` selects an explicit ASCII logical Host; otherwise the target authority must be ASCII and is used unchanged. Optional Region and temporary Token are sent only when supplied. There are no hostname/model/credential heuristics or configurable signing paths. See the [TC3 contract](https://cloud.tencent.cn/document/product/213/30654).

The default request limit is 1 MiB and response limit is 32 MiB, both configurable and part of provider identity. Success requires complete bounded UTF-8 JSON, normal EOF, valid declared length, unchanged actual method/target/body/signing headers, successful resource closure, unchanged source/principal, unexpired credentials and final cancellation checks. Default client Authorization/Host/X-TC/other-provider credential headers are refused. Configure the named factory client without redirects, automatic retries or authentication-mutating handlers. No SDK generation retry or failover is installed.

## Native SSE

`loki.LLM.Hunyuan.StreamAsync` uses the same complete target, External principal, once-per-call credentials and exact-byte TC3 rules as complete JSON. Requests are frozen before credential waits. Responses must be UTF-8 `text/event-stream`; a complete JSON response is never projected into synthetic events. Full HTTP or native errors keep their original body/status/media, and native `ErrorMsg` failures retain the JSON event data.

Every `HunyuanEvent.Data` preserves the original flat native object, including Id, varying Created, disclaimer Note, reasoning, raw tool fragments/IDs, usage, search/recommendation and unknown fields. Event name, inherited SSE ID and retry hints remain separate; retry hints never reconnect. Tool argument fragments remain strings and are not parsed, joined, executed or assigned new IDs. Native Id must stay stable. Created can change across records. The full-result Response envelope is not added to stream records.

Missing Index is accepted only in unambiguous single-choice mode and remains absent in JSON. Internal state uses implicit zero; a stream that mixes that mode with other indices fails rather than guessing associations. Multiple choices require explicit nonnegative indices and retain original order, including sparse indices. Empty/null/absent FinishReason is nonterminal; each observed choice needs a nonempty native finish reason. Native `sensitive`, `tool_calls` and future reason strings are retained unchanged. Duplicate terminals, ambiguous/repeated indices, changed Id and late known Content/ReasoningContent/ToolCalls generation fail explicitly.

From the first choice finish onward, every native record is buffered in order, including generation from other unfinished choices and metadata-only tails. The entire buffer is withheld until all observed choices finish and normal EOF, byte/length/encoding checks, async source/HTTP/client closure, signed-wire integrity, source/principal/expiry and final cancellation checks pass. A late failure never releases that terminal buffer. Ordinary preterminal increments already consumed remain partial output. Fully enumerate for validated completion; early consumer disposal closes owned resources. The native protocol neither needs nor synthesizes an OpenAI `[DONE]`; that non-JSON sentinel is rejected in native Hunyuan mode.

`MaximumBufferedTerminalEvents` defaults to 128 and `MaximumTrackedChoices` to 64 when null. Exceeding a limit fails instead of dropping fields/records. `MaximumResponseBytes` bounds the entire raw SSE exchange as well as complete JSON. Explicit stream policies enter provider identity; null policies are omitted from option JSON to preserve existing default complete-call Session fingerprints.

## Explicit TokenHub compatible calls

For TokenHub's OpenAI-compatible API, register an `OpenAiLlmProvider` from the separate [OpenAI package](../Asgard.Loki.LLM.OpenAI/README.md), an API Key credential and a `LlmProtocol.ChatCompletions` target with an explicit API root such as `https://tokenhub.tencentmaas.com/v1/`. Call `loki.LLM.ChatCompletions`, retaining that protocol's native tools and SSE contract. A Key containing `|` is still an opaque Bearer value in that explicitly chosen profile. The SDK does not dispatch protocols by Key shape, borrow TC3 credentials, substitute hosts or migrate a bound Session. The production Tencent package has no OpenAI dependency. Compatible profile tests establish local protocol reuse, not service/model availability or a promise that every OpenAI endpoint exists on TokenHub.

## Availability and validation

The [native API reference](https://cloud.tencent.com/document/api/1729/105701) currently documents host `hunyuan.ai.tencentcloudapi.com` and announces retirement on **2026-12-21 16:33:34** (page timezone unspecified), checked 2026-10-20. The SDK uses the caller's explicit endpoint and available model; it does not extend availability or migrate models/APIs.

Validation uses independent native/compatible protocol windows and all eight Python TC3 signing vectors through captured complete/stream requests, fake HTTP and an in-memory distributed cache only. No real Tencent credentials, upstream model call, STS, service availability or model quality has been validated.
