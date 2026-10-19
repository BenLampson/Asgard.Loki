# Asgard.Loki.LLM.AWS

Native Bedrock Claude `InvokeModel` JSON/EventStream and explicit Converse JSON/EventStream with borrowed SigV4 or explicit Bedrock API Key/Bearer authentication for the existing LLM capability. This supplier package depends only on `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not reference another supplier, Conversion, an AWS identity SDK or another capability.

## Support matrix

| Operation | Status |
| --- | --- |
| Claude Messages through Bedrock `InvokeModel` | Complete native JSON |
| Explicit model ID, inference profile or model ARN | Preserved as one escaped model path parameter; availability is caller-owned |
| SigV4 HMAC-SHA256 | Implemented for the explicit Runtime host, signing region and service `bedrock` |
| Temporary access credentials | Explicit borrowed source with optional Session Token and expiry |
| Native `x-amzn-bedrock-*` request headers | Optional provider configuration, independently snapshotted, signed and bound |
| Explicit common-subset conversion | Separate `LLM.Conversion` decorator fixed to upstream ClaudeMessages |
| InvokeModelWithResponseStream | Native binary framing, typed headers/CRC, Claude events and delayed terminal publication |
| Converse complete JSON | Explicit native `BedrockConverse` protocol/client; same borrowed SigV4 source |
| ConverseStream | Native direct-JSON EventStream, original block/tool association and delayed terminal publication |
| Nova understanding-model InvokeModel | Native complete JSON through explicit BedrockNova protocol |
| Nova InvokeModelWithResponseStream | Native binary EventStream with full JSON envelopes |
| Nova ↔ Converse conversion | Optional complete JSON and common logical text/tool streams in the separate Conversion package; broader fields and mixed four-text-protocol mappings remain pending |
| Other native InvokeModel payloads | Not implemented; compatible models may use explicit Converse |
| Bedrock Runtime API Key/Bearer | Explicit constructor without a SigV4 source; all four documented native methods |
| Mantle Messages SigV4 JSON/SSE | Separate `AwsMantleLlmProvider` in this package |
| SigV4a, presigning | Not implemented |
| Credential discovery/login/role assumption, resource management, retries | Application-owned or unsupported |

## Initialization

For SigV4, the caller supplies an `IHttpClientFactory`, an explicit Session store/coordinator and an `IAwsLlmCredentialSource`. The source-taking constructor requires `LlmCredential.External` with a stable principal assertion; ordinary `ApiKey` and anonymous credentials are rejected by that mode. The separate API Key constructor is documented below. A plain Key is never parsed as an access-key/secret pair.

```csharp
var provider = new AwsLlmProvider(httpFactory, credentialSource,
    new AwsLlmOptions { Region = "us-east-1" });
await using var client = new LokiClientBuilder().AddLLM(new()
{
    Providers = [provider],
    Credentials = [LlmCredential.External("aws-principal", "stable-role-principal")],
    SessionStore = sessionStore,
    Routes = [new()
    {
        Model = "public-model",
        Targets = [new()
        {
            Id = "bedrock-target", ProviderId = provider.Id,
            CredentialId = "aws-principal", Protocols = LlmProtocol.ClaudeMessages,
            BaseAddress = new Uri("https://bedrock-runtime.us-east-1.amazonaws.com/"),
            UpstreamModel = "YOUR_ENABLED_CLAUDE_MODEL_OR_PROFILE_ID"
        }]
    }]
}).Build();
var result = await client.LLM.ClaudeMessages.CreateAsync(new()
{
    Model = "public-model", MaxTokens = 512,
    Messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hello" })
}, new() { SessionId = "bedrock-conversation" });
```

`BaseAddress` is the explicit Runtime root, including any caller-owned proxy prefix. It cannot include user information, query or fragment. Signing region is explicit and is never inferred from a hostname, ARN or environment. `UpstreamModel` is the unchanged actual ID/profile/ARN; the provider does not add a regional prefix, rewrite model revisions or discover entitlements. ARN slashes are encoded within a single model parameter, and non-S3 SigV4 canonicalization double-encodes the transmitted path. The configured host is the signed host; a proxy that changes it must handle its own upstream authentication.

The named HTTP client defaults to `Asgard.Loki.LLM.AWS`. Factories and credential sources are borrowed; per-call clients, requests and responses are disposed. Configure timeouts and disable unsafe automatic retries/hedging in the named client. No model request, credential acquisition, endpoint selection or clock-skew adjustment is retried by the provider.

## Credentials and Session identity

An application implements `IAwsLlmCredentialSource.GetCredentialsAsync(principalIdentity, token)` and returns immutable `AwsLlmSigningCredentials` with the requested stable principal, raw access-key ID, secret key, expiry and optional Session Token. The source owns login, role assumptions, refresh and actual principal verification; the SDK checks the returned assertion, source identity and expiry. Long-lived credentials may use an appropriately explicit expiry. No default AWS profile, environment key, metadata service or other project's credential is read.

The source is called once for each explicit generation. Its `BindingIdentity` must identify stable account/role/credential-source configuration without secrets. Changes require a new provider; an incorrect principal, expired/null result, acquisition failure or configuration drift fails without a second acquisition or HTTP attempt. Signing uses the explicit clock immediately before sending, after factory creation, and rechecks source/expiry at that boundary. Derived HMAC keys are cleared after signing and are not cached. Default credential JSON/display omits principal, access key, secret and token.

Sessions bind the complete Runtime root/proxy, actual model, External principal, stable source and provider configuration (region, body version, byte limit, native headers and client name). Normal temporary Key/Token rotation leaves that binding stable. Changing principal, region, source, model, server or configuration rejects the old Session. No temporary Key, Token or signature enters its fingerprint/cache.

Selected Authorization/host/date/payload-hash and selected Session Token override same-kind defaults. An unselected temporary token, other Key authentication, unselected `x-amz-*` header or unconfigured `x-amzn-bedrock-*` request setting fails before HTTP. Optional `AwsLlmOptions.RequestHeaders` accepts only native `x-amzn-bedrock-*` names and ASCII values; the provider copies the collection, canonicalizes names, rejects duplicates/control characters and signs all configured values. It transports native guardrail/performance/trace settings without implementing application policy, billing or observability storage.

## Native fidelity and limits

The provider sends `POST model/{escaped-model}/invoke` with JSON Accept/Content-Type. It removes SDK `model`/`stream` body routing fields and adds the selected `anthropic_version` (default `bedrock-2023-05-31`); a conflicting caller-supplied version fails before credentials are acquired. All other native messages/system/tools/result IDs, signed/redacted thinking, native beta/schema/cache/usage fields and unknown values remain unchanged. No default token budget, history replay, tool execution or cross-protocol conversion is injected. Retaining a field is a transport guarantee, not proof that every Bedrock model accepts it.

`MaximumBodyBytes` defaults to 32 MiB for complete/error response bytes. Strict original Unicode/UTF-8, duplicate properties, JSON escapes, Content-Length, normal EOF and response/client disposal are checked before publishing success, followed by final cancellation. HTTP and native root errors retain status and original body. EventStream remains rejected as a complete JSON representation; the separate native stream method below decodes its binary frames. No ordinary SSE is substituted for AWS binary framing.

## Native binary EventStream

Use the same target/provider through `client.LLM.ClaudeMessages.StreamAsync(request, callOptions, cancellationToken)`. The method posts to `model/{escaped-model}/invoke-with-response-stream`, keeps the same Claude JSON body/version policy, and signs the selected EventStream Accept plus `x-amzn-bedrock-accept: application/json` headers. A conflicting explicit non-JSON model Accept fails before credential acquisition. The HTTP result must be `application/vnd.amazon.eventstream`; an optional model-content header must describe JSON. JSON or SSE responses are not simulated as binary streams.

```csharp
await foreach (var item in client.LLM.ClaudeMessages.StreamAsync(request, callOptions, cancellationToken))
{
    // Data stays native Claude JSON; outer transport metadata is a separate supplier-owned view.
    AwsEventStreamFrame? frame = item.GetAwsEventStreamFrame();
    JsonElement native = item.Data;
    JsonElement? typedHeaders = frame?.Headers;
}
```

Each frame requires complete big-endian lengths, prelude CRC32 and message CRC32 before publication. All ten fixed header types, exact signed integers/timestamps, strings, binary values and UUIDs are preserved. Duplicate/invalid headers and malformed framing fail. These are protocol checks; the SDK does not impose Smithy's server-only 24 MiB payload or 128 KiB header-size validation. Explicit local options separately bound raw total bytes (32 MiB), allocated frame bytes (8 MiB), headers per frame (128), Claude content-block associations (128) and buffered terminal-tail events (128); applications may configure appropriate limits. The header-count limit also bounds decoded-object expansion.

For `chunk`, native Base64 bytes decode to unchanged Claude event JSON. Raw outer payload, including unknown envelope fields, stays available through the frame view. Native known/unknown Claude JSON and JSON-valued future outer events remain available in order; no tool parameters, text, thinking or signatures are assembled. Original block indices and tool IDs are validated, not renumbered. A first stop reason/message_stop or native failure starts bounded terminal buffering, including metrics, unknown events and tails. Native message completion/failure, EOF, Content-Length, response/client disposal and final cancellation all precede publication of that tail. Missing/repeated/reopened blocks, wrong known delta association, changed stop reason, missing message_stop or late errors cannot synthesize success.

`AwsEventStreamFrame.Headers` is a name/type/value array and preserves numeric types/precision; `WireBytes` and `Payload` return independent copies. The event's `EventName` is the outer AWS event name (usually chunk); its `Type` comes from native Claude JSON. No SSE ID/retry is invented. Provider-local weak associations keep the frame alive only with its event/exception, without a Session registry or retained global history. Newly created converted events do not carry that native frame view; Conversion continues to expose its existing upstream JSON/diagnostics contract. The raw frame view adds no supplier dependency to the core.

Modeled exception/unmodeled error frames throw `LlmUpstreamException` with the original HTTP status and original payload text; error code/type/message and all raw bytes remain in `exception.GetAwsEventStreamFrame()`. An inner Claude error before potential success remains a native failure event withheld until source closure; a late error discards held success. Other failures while processing a valid frame also expose it. HTTP failures, invalid/unreadable frames and whole-stream closure failures have no valid associated frame. Unknown non-JSON outer payloads fail explicitly with their raw valid frame available, rather than being silently dropped or coerced into Claude JSON.

The new EventStream state/resource policy uses a new AWS provider binding domain. Sessions from the earlier JSON-only AWS checkpoint must be recreated. Core credential fingerprints, conversion-v24 and other providers are unchanged. Sources/factories stay borrowed; no reconnect, credential reacquisition, clock correction or generation retry occurs.

## Explicit native Converse JSON

Converse is an independent Bedrock wire protocol, available through `client.LLM.BedrockConverse.ConverseAsync` with `BedrockConverseRequest` / `BedrockConverseResponse`. Declare `LlmProtocol.BedrockConverse` on the target. Existing Claude routes do not silently acquire it, and the conversion decorator does not advertise it. The AWS provider implements the core `IBedrockConverseAdapter` contract without adding provider references to the capability package.

```csharp
var provider = new AwsLlmProvider(httpFactory, credentialSource,
    new AwsLlmOptions { Region = "us-east-1" });
await using var client = new LokiClientBuilder().AddLLM(new()
{
    Providers = [provider],
    Credentials = [LlmCredential.External("aws-principal", "stable-role-principal")],
    SessionStore = sessionStore,
    Routes = [new()
    {
        Model = "public-model",
        Targets = [new()
        {
            Id = "converse-target", ProviderId = provider.Id,
            CredentialId = "aws-principal", Protocols = LlmProtocol.BedrockConverse,
            BaseAddress = new Uri("https://bedrock-runtime.us-east-1.amazonaws.com/"),
            UpstreamModel = "YOUR_ENABLED_CONVERSE_MODEL_OR_PROFILE_ID"
        }]
    }]
}).Build();
var request = JsonSerializer.Deserialize<BedrockConverseRequest>(
    """{"model":"public-model","messages":[{"role":"user","content":[{"text":"Hello"}]}],"inferenceConfig":{"maxTokens":512}}""")!;
var result = await client.LLM.BedrockConverse.ConverseAsync(request,
    new() { SessionId = "converse-conversation" });
```

The request DTO has the logical `Model` and JSON extension `Parameters` for every native field, preserving omission versus explicit null. This supports prompt-ARN requests that omit messages, as well as messages, system/cache blocks, toolUse/toolResult IDs/results, reasoningText/signatures/redacted bytes, image/document/video references, inferenceConfig, additionalModelRequestFields, native outputConfig and unknown values. The provider posts to `model/{escaped-actual-model}/converse`, removes only the SDK model field, and injects no stream flag, Anthropic version, token default or protocol conversion. An explicit unknown stream/anthropic_version body value stays caller-owned; it does not choose the method. Native Converse JSON-schema output uses `outputConfig.textFormat.structure.jsonSchema.schema` as a string, not Claude's schema-object shape. No schema/model-specific equivalence is inferred.

Complete native output/stopReason/usage/metrics/trace/additionalModelResponseFields are returned unchanged. Native trace/guardrail/service-tier data is passive JSON, without policy enforcement, storage or billing. Model compatibility/availability is caller-selected, including supported Nova models through Converse; native Nova InvokeModel is a separate unimplemented dialect. Media stays raw JSON/Base64/reference data; the SDK fetches/uploads/decodes no media.

This operation reuses the same provider-private strict bounded JSON, original HTTP/root-error preservation, source/expiry/clock/headers/signing, EOF/length/disposal and final cancellation checks. Claude-only type=error event interpretation is not applied to an unknown Converse type field. All configured region/source/headers/policy still participate in full target/principal binding. Sessions cannot change protocol, even for a target explicitly declaring both ClaudeMessages and BedrockConverse. Existing numeric protocol values, core fingerprint algorithms, AWS default binding bytes, and conversion-v24 are unchanged; modifying an existing target's declared protocol flags changes that target's fingerprint as usual. No existing Session is transparently migrated.

Explicit cross-protocol Converse mapping remains pending; the independent native stream operation is documented below. The 41 new provider cases use three authored full request/wire/result fixtures and four independent final signature vectors covering eight public/direct HTTP calls, prompt-only requests, unknown-field isolation, snapshots, credentials/target recovery, changed identity/protocol, original errors and late encoding/bounds/disposal/cancellation failures. Eleven additional core contract cases use an application-owned adapter and no AWS dependency. No real Converse/Nova model call or live field acceptance is claimed.

## Native ConverseStream

Use `client.LLM.BedrockConverse.StreamAsync(request, callOptions, cancellationToken)` with the same explicit Converse target. It posts to `model/{escaped-actual-model}/converse-stream`, signs EventStream Accept and keeps the Converse body policy above. It injects neither a body stream flag nor Claude's `x-amzn-bedrock-accept` header. A JSON/SSE response cannot stand in for the binary representation.

The shared supplier-private frame reader checks CRCs, lengths and typed headers. Converse payloads are direct native JSON objects, without Claude Base64 chunk decoding. `BedrockConverseEvent.EventName` and `Type` both use the native transport event name, including through a `LlmJsonEvent` reference; an unknown payload type/error/bytes field stays raw. Claude and other events retain their existing type rules. `item.GetAwsEventStreamFrame()` exposes the original frame on Converse events too. Transport exception/error and known service-error events fail with the original HTTP status/payload and valid raw frame; a native image-delta error remains image data.

A separate private Converse state checks messageStart, block indices/kinds, tool IDs, block stops, messageStop and metadata. Native text/reasoning deltas may implicitly begin a block; toolUse/toolResult/image deltas require the corresponding explicit start. Tool-result IDs are retained without inventing a history lookup or executing tools. Reasoning text/signatures/redacted data, fragmented tool input, citations, image sources, unknown enum values/fields/events and exact JSON numbers remain unchanged. No content aggregation or synthetic done event is provided.

The first messageStop and every following event are held until metadata with native usage/metrics, all closed blocks, EOF/Content-Length, normal response/client disposal and final cancellation checks succeed. Missing/repeated/reopened/mismatched state, late native/transport errors, corrupt tails or cancellation discard held success. Existing frame/header/total/tail policies apply; optional `MaximumTrackedConverseBlocks` defaults effectively to 128 and separately bounds explicit/implicit block associations, including sparse indices. Its null default is omitted from provider identity serialization, preserving exact preceding default AWS binding bytes. An explicit policy value changes the binding; normal Key/Token rotation and JSON/stream continuation keep the full target/principal fixed.

The 71 new provider cases use an independently encoded 27-frame (4769-byte) binary fixture with complete native event/header/raw-payload manifests and four final signature vectors exercised through eight public/direct HTTP paths. They cover implicit and explicit starts, tool/result/image associations, native unknown type isolation, original errors, snapshots, JSON/stream Session recovery, resource policies and late failures. One additional core case checks transport-name precedence through the base event view. No live Converse/Nova acceptance, cloud login or remote CI is claimed.

## Explicit Bedrock Runtime API Key authentication

Construct `new AwsLlmProvider(httpFactory, options)` **without** a signing source to select native Bedrock API Key/Bearer authentication. The existing source-taking constructor still selects SigV4. Each instance supports exactly one credential kind; there is no fallback or credential-format guessing. Use the existing `new LlmCredential(id, apiKey)` for Key mode. Anonymous and External credentials are rejected by that mode before HTTP; the SigV4 constructor continues to reject ordinary API Keys.

```csharp
var keyProvider = new AwsLlmProvider(httpFactory,
    new AwsLlmOptions { Region = "us-east-1" });
await using var keyClient = new LokiClientBuilder().AddLLM(new()
{
    Providers = [keyProvider],
    Credentials = [new("bedrock-key", "YOUR_BEDROCK_API_KEY")],
    SessionStore = sessionStore,
    Routes = [new()
    {
        Model = "public-model",
        Targets = [new()
        {
            Id = "bedrock-key-target", ProviderId = keyProvider.Id,
            CredentialId = "bedrock-key", Protocols = LlmProtocol.BedrockConverse,
            BaseAddress = new Uri("https://bedrock-runtime.us-east-1.amazonaws.com/"),
            UpstreamModel = "YOUR_ENABLED_CONVERSE_MODEL_OR_PROFILE_ID"
        }]
    }]
}).Build();
var keyRequest = JsonSerializer.Deserialize<BedrockConverseRequest>(
    """{"model":"public-model","messages":[{"role":"user","content":[{"text":"Hello"}]}]}""")!;
var keyResult = await keyClient.LLM.BedrockConverse.ConverseAsync(keyRequest,
    new() { SessionId = "key-conversation" });
```

The explicit target root/proxy and actual model ID/profile/ARN still select the request. Region remains required provider configuration and participates in binding; Key mode does not use it to rewrite the endpoint or parse a combined Key/region string. The opaque Key becomes one `Authorization: Bearer` value; whitespace/control/non-ASCII header values fail without trimming, decoding or secret echo. No environment variable, AWS profile, login, token source, key issuance, principal lookup or automatic refresh is used. Use the source-taking SigV4 mode when the application needs its existing stable-principal temporary-credential contract.

Key mode shares the same documented Claude InvokeModel JSON/EventStream and independent Converse JSON/EventStream methods, original bodies/results, tool IDs, reasoning/schema/unknown fields, CRC/typed headers, bounds and delayed terminals. It emits no SigV4 date, payload hash, Session Token or signature. Selected Authorization/actual Host override same-kind client defaults. Unselected other-Key, `x-amz-*` and native Bedrock control defaults fail before HTTP. Explicit native options/method headers keep their selected values. The HTTP factory stays borrowed; per-call resources are released normally and generation never retries or falls back to signing.

Key mode has a separate non-secret provider binding domain. Existing SigV4 default binding bytes and all core/conversion identities are unchanged. Complete-target/API-Key fingerprints follow the existing core contract: changing the Key, credential kind, server/model, region, policy or named client rejects an old Session. An unchanged Key/configuration supports cross-client JSON/stream recovery to the same selected target. The SDK cannot assert a stable principal or expiry from an opaque Key; application-directed Key renewal requires the normal new binding rather than silently continuing an old Session.

The 81 new offline cases include eight independently authored HTTP manifests exercised through sixteen public/direct, model/ARN and operation combinations, reusing the independent full native JSON/binary/event/frame fixtures. They verify exact paths/headers/bodies/results and raw frames, pre-factory snapshots, auth-kind/default-header isolation, Host/IPv6/IDN selection, secret-free credentials/cache, key/config/mode binding changes, cross-mode target recovery, original HTTP/frame errors, late read/disposal/cancellation and early exit. Three public Chat-through-Claude cases verify the existing explicit common-text converter with Bearer, including late-failure completion rejection. Tests do not prove real API Key authentication, model entitlement or field acceptance.

This checkpoint covers Bedrock Runtime native operations. Mantle, Runtime OpenAI-compatible/Anthropic-native Messages endpoint variants, native Nova InvokeModel, SigV4a, presigning and bidirectional invocation are separate contracts or unimplemented operations. AWS documents API Key restrictions separately, including bidirectional invocation; no account/service-management operation or gateway is implemented here.

## Verification status

381 offline xUnit v3 cases (91 Claude JSON/signing, 97 Claude EventStream, 41 Converse JSON, 71 ConverseStream and 81 Bedrock API Key cases) cover independent caller/wire/result JSON, eight full signature vectors (public SDK/direct adapter, model ID/ARN, long-lived/temporary credentials), the published AWS IAM signature, exact path escaping and header normalization, snapshots before acquisition, Key/Token rotation/fixed-target Session recovery, changed identities/configuration, default-header isolation, source/cancellation/expiry failures, raw bounds/encoding/EOF/disposal and explicit common-text conversion. Tests use substitute HTTP, deterministic time and `MemoryDistributedCache` with an explicit coordinator. The signature goldens were calculated independently using Python `hashlib`/HMAC against authored wire bodies, including explicitly declared public DTO property order; they are not captured provider output.

The 97 additional cases use a 16-frame independent Python struct/zlib binary fixture, full expected native/outer header/payload manifests and eight independent stream signature vectors. They cover every header type, CRC/length/encoding/Base64, original block/tool associations and unknown fields, snapshots, cross-mode credential rotation/Session recovery, source disposal/cancellation, strict local limits, original inner/outer errors and explicit converted text streams with late-failure finish rejection. No AWS login, real Key, role refresh, model entitlement, live call, Linux execution or new GitHub Actions run is claimed. The existing Volcengine live-test environment contract is unchanged. Model-specific field acceptance and response quality remain separate live integration work.

Primary references: [Bedrock API Key use](https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys-use.html), [Runtime and Mantle endpoints](https://docs.aws.amazon.com/bedrock/latest/userguide/endpoints.html), [ConverseStream API](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_ConverseStream.html), [native stream order](https://docs.aws.amazon.com/bedrock/latest/userguide/conversation-inference.html), [Converse API](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_Converse.html), [native JSON schema definition](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_JsonSchemaDefinition.html), [InvokeModelWithResponseStream](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_InvokeModelWithResponseStream.html), [Amazon EventStream specification](https://smithy.io/2.0/aws/amazon-eventstream.html), [InvokeModel path and headers](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_InvokeModel.html), [Bedrock Claude native request/response](https://docs.aws.amazon.com/bedrock/latest/userguide/model-parameters-anthropic-claude-messages-request-response.html), [SigV4 signing](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_sigv-create-signed-request.html), [AWS SDK canonicalization](https://github.com/aws/aws-sdk-net/blob/main/sdk/src/Core/Amazon.Runtime/Internal/Auth/AWS4Signer.cs), [published IAM signing example](https://aws.amazon.com/ko/blogs/korea/aws-api-call-1/).

## Separate Anthropic-native endpoint reuse

The [Anthropic provider](../Asgard.Loki.LLM.Anthropic/README.md#bedrock-messages-endpoint-reuse) now has independent Runtime/Mantle `/anthropic/v1/messages` JSON/SSE fixtures with explicit `x-api-key` mode and a caller-selected root/model. Those operations belong to the existing native Messages supplier and do not introduce a dependency from this AWS package. They are separate from this package's InvokeModel/Converse JSON/EventStream. Mantle SigV4 is implemented by the separate provider below; Nova InvokeModel and cloud OpenAI-compatible endpoints remain pending. The fixture checkpoint does not establish live cloud access or optional Bearer acceptance.

## Explicit Mantle SigV4 Messages

`AwsMantleLlmProvider` implements the existing `IClaudeMessagesAdapter` and supports only `LlmCredential.External`. It is separate from `AwsLlmProvider`: Mantle uses native Messages JSON/SSE and the fixed signing service **`bedrock-mantle`**. Runtime InvokeModel/Converse continue using `bedrock` with their existing request shapes, identities and signatures. Neither provider switches API by model name or failure.

```csharp
var mantle = new AwsMantleLlmProvider(httpFactory, credentialSource,
    new AwsMantleLlmOptions
    {
        Region = "us-east-1",
        RequestHeaders = new Dictionary<string, string>
        {
            ["anthropic-beta"] = selectedBeta,
            ["anthropic-workspace-id"] = existingWorkspaceId
        }
    });
var target = new LlmTarget
{
    Id = "mantle-primary", ProviderId = mantle.Id,
    BaseAddress = new Uri("https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/"),
    UpstreamModel = actualMantleModel, CredentialId = "mantle-principal",
    Protocols = LlmProtocol.ClaudeMessages
};
// Credentials = [LlmCredential.External("mantle-principal", stablePrincipalIdentity)]
// Register provider, target, Session cache/coordinator and caller-owned source as usual.
```

`IAwsLlmCredentialSource` supplies one `AwsLlmSigningCredentials` per complete call. Source identity and returned principal must stay consistent; credentials must be unexpired both after acquisition and before signing after client creation. The application owns login, role assumption and refresh. The SDK does not inspect profiles/environment, split gateway credential strings, cache derived keys, retry or switch targets. Ordinary API Key/anonymous identities fail before acquisition. Native Key-mode Mantle Messages can separately reuse the [Anthropic supplier](../Asgard.Loki.LLM.Anthropic/README.md#bedrock-messages-endpoint-reuse); this SigV4 adapter does not borrow that package or a default Bearer value.

The caller supplies the exact root prefix/model. Only `messages` is appended; only model and method stream flag are fixed in the native body. There is no automatic `anthropic.` prefix, region/model rewrite, InvokeModel version injection, or deletion of caller fields. Explicit caller extensions remain raw, including fields a specific endpoint may reject. The default header version is `2023-06-01`. Optional `anthropic-*` (except the separately configured version) and `x-amzn-bedrock-*` headers are independently snapshotted, signed and bound. Host and selected signature headers override matching client defaults; unselected authentication, AWS signing or native-control defaults fail before send. Configure any relevant native header explicitly in options; the SDK does not manage workspaces/projects.

Local limits default to 32 MiB raw response bytes, 128 tracked content blocks and 128 pending terminal/tail events. Complete JSON and SSE have strict UTF-8/duplicate-property/declared-length checks. Native sparse association keys, tool IDs and known delta kinds are validated without assembling tool arguments or altering signatures/encrypted items. Stop-reason candidates, native stop/error and unknown metadata wait for real EOF, async source disposal, response/client disposal and final cancellation. Initial content remains incremental. Late errors discard held success, preserving original status/body; early exit releases this call's resources. SSE has no AWS binary frame attachment or automatic reconnect. These transport checks do not certify every native Claude schema or model capability.

The new `aws-mantle-messages-sigv4-v1` binding includes explicit signing region/service, version, native headers, limits, named client and stable source. Short-lived Key/Session Token rotation for the same principal/source does not rebind the Session. Changing stable source/config/principal or the complete target requires a new binding. Existing Runtime/AWS and other supplier identities, core fingerprints and conversion-v25 algorithms remain unchanged. Optional explicit conversion is still composed separately by the application.

Four independent full native resources and sixteen Python SHA256/HMAC final HTTP vectors verify paths (including encoded proxy prefixes/ports), exact body bytes/property order, selected model/auth/native headers, permanent/temporary credentials, JSON/SSE and direct/public SDK paths. 107 new offline cases cover snapshots, source/cancellation/expiry/default isolation, full-target recovery/rotation and original/late faults, plus explicit conversion delegation. Current AWS total is **488**, repository **5918**, still 28 packages. No live AWS account/model, Linux or remote Actions run is claimed. Native Nova, remaining cloud-compatible operations and broader roadmap items remain pending.

References: [AWS native Messages contract](https://docs.aws.amazon.com/bedrock/latest/userguide/inference-messages-api.html), [SigV4 request construction](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_sigv-create-signed-request.html). The pinned New API Mantle source signs service `bedrock-mantle`; its gateway root/model/credential normalization is deliberately caller configuration here.

## Native Nova InvokeModel JSON

`AwsLlmProvider` now implements `IBedrockNovaAdapter` in this existing package. This is a separate explicitly selected protocol from Claude and Converse; model prefixes never select the API. Both existing constructors apply: borrowed SigV4 with `LlmCredential.External`, or explicit Runtime API Key/Bearer with an ordinary API Key credential. No new AWS source, SDK dependency or automatic credential path is introduced.

```csharp
var provider = new AwsLlmProvider(httpFactory, credentialSource,
    new AwsLlmOptions { Region = "us-east-1" });
var target = new LlmTarget
{
    Id = "nova-primary", ProviderId = provider.Id,
    BaseAddress = new Uri("https://bedrock-runtime.us-east-1.amazonaws.com/"),
    UpstreamModel = actualNovaModelOrArn, CredentialId = "nova-principal",
    Protocols = LlmProtocol.BedrockNova
};
// Register target/route, External credential, provider and the existing Session dependencies.
var result = await loki.LLM.BedrockNova.InvokeAsync(new BedrockNovaRequest
{
    Model = "assistant",
    Parameters = new Dictionary<string, System.Text.Json.JsonElement>
    {
        ["messages"] = System.Text.Json.JsonSerializer.SerializeToElement(
            new[] { new { role = "user", content = new[] { new { text = "Hello" } } } }),
        ["inferenceConfig"] = System.Text.Json.JsonSerializer.SerializeToElement(
            new { maxTokens = 512, topK = 0 })
    }
}, new LlmCallOptions { SessionId = "nova-conversation" }, cancellationToken);
```

The public alias must be configured in a route. Supply the exact root/model/ARN and native v1/v2 body for that selected model. In particular, add `schemaVersion: messages-v1` explicitly when required by the selected v1 contract; it is not inserted into v2 requests. The adapter removes only the SDK `model` field, escapes the actual model/ARN as one path parameter and calls `model/{id}/invoke`. It does not add Claude's `anthropic_version`, drop source `stream`/unknown native fields, infer model generation, relocate topK/reasoning settings to Converse fields or certify native model limits. Original fields may still be rejected by the chosen upstream model.

Complete output retains every content block, tool-use/result IDs, reasoning/redacted/opaque items, native stop reason, usage/metrics, exact numbers and unknown fields. It neither extracts the first text block nor recomputes totals or changes the finish reason. A root business `type` extension is not interpreted as a Claude event. HTTP/root platform errors retain status/body, including HTTP 200; strict bytes/UTF-8/JSON/media/length checks apply. Normal EOF, async/sync disposal and final cancellation precede success. Generation is never retried or moved to another target. Borrowed sources/factories remain caller-owned.

`InvokeNovaAsync` is the direct supplier method; the public SDK uses the explicit adapter interface. The subsequent native stream checkpoint now implements `NovaStreamAsync` and the adapter `StreamAsync`, described below. Calls remain explicit rather than falling back to Claude, Converse or a nonstream call. Optional Nova ↔ Converse complete JSON conversion is now available in the separate Conversion package; common logical text/tool streams are also supported there; other protocol pairs and broader native stream content remain unsupported. Original Runtime/Key/Mantle binding bytes and signature vectors, core fingerprint algorithms and conversion-v25 are unchanged. Sessions bind protocol 128 as well as the full target and cannot switch protocols.

Independent complete v1/v2 native resources and sixteen Python SHA256/HMAC wire manifests verify raw body bytes/property order, model/ARN paths, direct/public calls and temporary SigV4 credentials. Explicit Bearer cases, snapshots, errors/byte limits/disposal/cancellation and stable full-target recovery have direct regressions. This JSON checkpoint adds 51 AWS, 12 core and two conversion-boundary cases: **5983 total**, **539 AWS**, **191 core**, **1917 conversion**, 28 packages. Model/account/media/reasoning behavior, binary streaming and cross-protocol mappings are not established by offline JSON tests.

References: [Nova v1 Invoke](https://docs.aws.amazon.com/nova/latest/userguide/using-invoke-api.html), [Nova 2 native Invoke](https://docs.aws.amazon.com/nova/latest/nova2-userguide/using-invoke-api.html). Unlike the pinned New API text wrapper, the SDK preserves the complete caller-native request and all response blocks.

## Native Nova InvokeModelWithResponseStream

Use `loki.LLM.BedrockNova.StreamAsync(request, callOptions, cancellationToken)` with the same explicitly bound target/credential and native body as complete JSON. The direct supplier method is `NovaStreamAsync`. Both borrowed SigV4 and the explicit API Key constructor are supported. The provider chooses `invoke-with-response-stream`, binary Accept and JSON model-content selection; it does not insert a body `stream`/version field or reinterpret caller-native parameters. There is no reconnect, resubmission or protocol switch.

Native Invoke stream frames wrap a Base64 JSON model envelope in `chunk.bytes`. `BedrockNovaEvent.Data` preserves that **whole decoded envelope**; it is not unwrapped into Converse or Claude data. `EventName` is the original outer name; `Type` is the recognized inner message/content/stop/metadata name or the outer name for unknown data. Unknown root `type`/`error`, inner properties and independent outer events remain raw, not guessed errors or successful terminals. `GetAwsEventStreamFrame()` on the original event/semantic exception provides the complete CRC-checked frame, ordered typed headers and original outer payload including Base64/unknown wrapper fields. New/copied cross-protocol events do not inherit that attachment. Unknown binary payloads fail explicitly with their valid raw frame available.

Length/prelude CRC/message CRC, strict UTF-8/JSON, Base64 and raw total/frame/header policies are checked before publication. Supplier-local Nova state tracks original block indices and ordinary tool IDs/names/types without argument aggregation or array allocation by sparse index. Text and reasoning may begin implicitly. Known orphan/duplicate/closed blocks, incompatible deltas, ambiguous envelopes, repeated/reopened/missing message closure fail. Unconfirmed native reasoning/signature/media/tool-result extensions are preserved without importing another protocol's field constraints. That passthrough does not certify every future native payload.

`messageStop`, usage metadata and all subsequent tails are held until native metadata, normal EOF/declared lengths, async/sync disposal and final cancellation succeed. Usage metadata requires its native object; optional metrics, when present, must be an object. No stop or usage is synthesized, and counters/stop reasons remain original. Initial events are incremental and early exit releases only this call. HTTP/platform/outer or known inner service errors retain original status/body/valid frame; late errors and corrupt/truncated tails cannot release held success.

Optional `AwsLlmOptions.MaximumTrackedNovaBlocks` defaults effectively to 128. Its nullable default is omitted from binding serialization, preserving prior Runtime/Key identities and exact default signatures. An explicit value participates in complete-target identity and invalidates changed Sessions normally. Existing frame/header/byte/tail limits apply; temporary key/token rotation for the same stable source/principal does not change binding. Core/other supplier/conversion-v25 algorithms and dependencies remain unchanged.

A separately authored 17-frame Python struct/zlib binary fixture includes all fixed header types, original Base64 envelopes, unknown inner/outer metadata, reasoning and interleaved same-name tool IDs. Sixteen independent Python HTTP/HMAC vectors verify v1/v2, model/ARN, direct/public and temporary signing. 86 new offline cases bring totals to **6069** (**625 AWS**, 28 packages), covering exact full wire/frame data, explicit Bearer, source lifecycle, native/CRC/JSON/Base64 errors, immutable views, resource/async disposal/cancellation, policy/binding and JSON-to-stream full-target recovery. The earlier two unsupported-stream tests are now pre-cancellation tests; no source validation was removed. No live account/model, Linux or remote Actions claim is made. Nova explicit conversion and broader roadmap families remain pending. See [Nova native streaming](https://docs.aws.amazon.com/nova/latest/nova2-userguide/streaming-responses.html).

### Optional complete Nova / Converse conversion

Compose `ConvertingBedrockLlmProvider` from `Asgard.Loki.LLM.Conversion` around the selected native AWS adapter; [initialization, exact matrix and format/layout declarations](../Asgard.Loki.LLM.Conversion/README.md#explicit-nova--converse-complete-json) are documented there. The AWS package does not reference Conversion or enable it automatically. Its native clients still preserve all supplier-specific JSON and binary frames. The decorator has a separate binding domain; format/layout changes require a fresh Session while existing AWS and four-protocol conversion identities stay unchanged.

The 39 additional AWS cases include sixteen public complete HTTP paths over v1/v2, both explicit Converse layouts, both directions and SigV4/Runtime Bearer. Full native body goldens, auth delegation, tool/schema/precision, original output/metadata, no-HTTP rejections, Session changes, late JSON faults and original identity-stream frame attachments are verified offline. Current AWS total is 664, whole repository 6250, still 28 packages. Native transport and explicit conversion are separate support claims. No account/model/layout acceptance, live generation, Linux run or GitHub Actions execution is claimed.

### Optional native logical stream conversion

The optional [Conversion SDK wrapper](../Asgard.Loki.LLM.Conversion/README.md#public-sdk-native-stream-composition) now composes this supplier's actual native EventStream adapter with a separate pure logical mapper. AWS production still has no Conversion dependency. Selected native paths, authentication, CRC/framing and supplier resource checks remain here; exact original frame attachments remain available on native identity streams. Converted target events have logical types/JSON and no fabricated AWS frame.

Fifty-eight new AWS cases bring this suite to 722 and the repository to 6461. Sixteen public calls verify full native requests/events over both native formats, both configured wrappers, both directions and SigV4/Bearer. Further cases cover strict/consented business metadata, late binary/native/read/sync-dispose/cancellation faults, source metrics, historical IDs, bounded local conversion state and actual policy-change Session rejection. A preceding exact default wrapper identity is retained. The separate conversion suite additionally exercises async-only source-iterator disposal failures; these new AWS cases do not certify every older supplier-internal async source-disposal path. No real account/model call, Linux or remote CI is claimed.
