# Asgard.Loki.LLM.AWS

Native Bedrock Claude `InvokeModel` for the existing LLM capability. This supplier package depends only on `Asgard.Loki.LLM` and standard Microsoft HTTP components. It does not reference another supplier, Conversion, an AWS identity SDK or another capability.

## Support matrix

| Operation | Status |
| --- | --- |
| Claude Messages through Bedrock `InvokeModel` | Complete native JSON |
| Explicit model ID, inference profile or model ARN | Preserved as one escaped model path parameter; availability is caller-owned |
| SigV4 HMAC-SHA256 | Implemented for the explicit Runtime host, signing region and service `bedrock` |
| Temporary access credentials | Explicit borrowed source with optional Session Token and expiry |
| Native `x-amzn-bedrock-*` request headers | Optional provider configuration, independently snapshotted, signed and bound |
| Explicit common-subset conversion | Separate `LLM.Conversion` decorator fixed to upstream ClaudeMessages |
| InvokeModelWithResponseStream | Not implemented; binary EventStream is a separate checkpoint |
| Converse/ConverseStream, Nova and other model payloads | Not implemented; no model-name-based protocol selection |
| Bedrock/Mantle Bearer API Keys, SigV4a, presigning | Not implemented |
| Credential discovery/login/role assumption, resource management, retries | Application-owned or unsupported |

## Initialization

The caller supplies an `IHttpClientFactory`, an explicit Session store/coordinator and an `IAwsLlmCredentialSource`. Use `LlmCredential.External` with a stable principal assertion; ordinary `ApiKey` and anonymous credentials are rejected. A plain Key is never parsed as an access-key/secret pair.

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

`MaximumBodyBytes` defaults to 32 MiB for complete/error response bytes. Strict original Unicode/UTF-8, duplicate properties, JSON escapes, Content-Length, normal EOF and response/client disposal are checked before publishing success, followed by final cancellation. HTTP and native root errors retain status and original body. EventStream is explicitly rejected as a complete JSON representation; streaming fails before acquiring credentials/HTTP until its dedicated native implementation is delivered. No ordinary SSE is substituted for AWS binary framing.

## Verification status

91 offline xUnit v3 cases cover independent caller/wire/result JSON, eight full signature vectors (public SDK/direct adapter, model ID/ARN, long-lived/temporary credentials), the published AWS IAM signature, exact path escaping and header normalization, snapshots before acquisition, Key/Token rotation/fixed-target Session recovery, changed identities/configuration, default-header isolation, source/cancellation/expiry failures, raw bounds/encoding/EOF/disposal and explicit common-text conversion. Tests use substitute HTTP, deterministic time and `MemoryDistributedCache` with an explicit coordinator. The signature goldens were calculated independently using Python `hashlib`/HMAC against authored wire bodies, including explicitly declared public DTO property order; they are not captured provider output.

No AWS login, real Key, role refresh, model entitlement, live call, Linux execution or new GitHub Actions run is claimed. The existing Volcengine live-test environment contract is unchanged. Model-specific field acceptance and response quality remain separate live integration work.

Primary references: [InvokeModel path and headers](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_InvokeModel.html), [Bedrock Claude native request/response](https://docs.aws.amazon.com/bedrock/latest/userguide/model-parameters-anthropic-claude-messages-request-response.html), [SigV4 signing](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_sigv-create-signed-request.html), [AWS SDK canonicalization](https://github.com/aws/aws-sdk-net/blob/main/sdk/src/Core/Amazon.Runtime/Internal/Auth/AWS4Signer.cs), [published IAM signing example](https://aws.amazon.com/ko/blogs/korea/aws-api-call-1/).
