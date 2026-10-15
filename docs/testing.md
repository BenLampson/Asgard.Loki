# Test configuration contract

This document defines the environment variables that live integration tests must use. It is a project requirement for future test fixtures, not a production configuration API. Library consumers continue to supply typed options and dependencies explicitly.

The current automated tests use substitute HTTP handlers and `MemoryDistributedCache`; they require no environment variables. Live integration fixtures have not yet been added. Setting these variables today does not turn the unit tests into real-provider calls. The repository does not automatically load `.env` files.

## Naming rules

1. Use uppercase ASCII and underscores with the prefix `ASGARD_LOKI_TEST_`. Do not implicitly read generic variables such as `OPENAI_API_KEY` or credentials from another project.
2. Provider settings follow `ASGARD_LOKI_TEST_{PROVIDER}_{CAPABILITY}_{SETTING}`. Current provider tokens are `VOLCENGINE` and `OPENAI`; capability tokens are `LLM`, `AUDIO` and `IMAGE`. Add names only when a test implementation supports that capability.
3. Keep capabilities independent. Speech and LLM do not share credentials implicitly, even for the same provider. OpenAI image and LLM tests also configure their keys separately; a tester may explicitly assign the same key to both variables when their account permits it.
4. `API_KEY`, `ACCESS_KEY` and `CONNECTION_STRING` identify secrets. `BASE_URL` is an API root including its version path; `ENDPOINT` is a complete operation URL. `MODEL`, `CHAT_MODEL` and `RESPONSES_MODEL` mean actual upstream model names or inference endpoint IDs, not public Loki route aliases.
5. For an additional target, insert a stable profile token before the setting: `ASGARD_LOKI_TEST_VOLCENGINE_LLM_SECONDARY_API_KEY`, `..._SECONDARY_BASE_URL`, `..._SECONDARY_CHAT_MODEL`. Each selected fixture declares the profiles it needs explicitly. A profile owns its complete server/model/credential combination; never treat multiple keys as proof that their server or model is identical. The default profile omits the profile token.

## Explicit opt-in and limits

| Variable | Default | Requirement |
| --- | --- | --- |
| `ASGARD_LOKI_TEST_LIVE` | `false` | Only `true` explicitly enables external integration calls. Merely configuring credentials must not trigger them. Accept `true`/`false` case-insensitively; reject other non-empty values. |
| `ASGARD_LOKI_TEST_MAX_REQUESTS` | `5` | Positive integer; maximum generation requests across one live-test invocation, including all selected cases and profiles. Enforce the budget before sending each request; do not reset it per case or retry automatically. |
| `ASGARD_LOKI_TEST_TIMEOUT_SECONDS` | `90` | Positive integer; deadline for each complete request, including fully consuming its stream. |

Live integration tests must use a separate test project or explicit integration entry point. The ordinary `eng/Test.ps1` and automatic CI remain offline even when these variables exist. Future live test projects must not match the ordinary `tests/**/*.Tests.csproj` discovery; use names ending in `.IntegrationTests.csproj`, exclude them from the ordinary solution, and document their explicit run command when implemented.

With live testing disabled, a live test must report that it was skipped, not report a successful provider invocation. After explicitly selecting a live scenario and enabling live testing, missing or invalid required configuration must fail before any network request. Report the missing variable names without their values. Validate the entire selected configuration first; do not publish partial success by silently skipping unconfigured selected cases. Do not instantiate or validate unrelated providers.

## LLM targets

For each selected provider (`VOLCENGINE` or `OPENAI`):

| Suffix after `ASGARD_LOKI_TEST_{PROVIDER}_LLM_` | Requirement | Maps to |
| --- | --- | --- |
| `API_KEY` | Required | Explicit `LlmCredential` for this target |
| `BASE_URL` | Required | `LlmTarget.BaseAddress`; HTTPS API root including version path |
| `CHAT_MODEL` | Required for selected Chat Completions cases | `LlmTarget.UpstreamModel` for Chat cases |
| `RESPONSES_MODEL` | Required for selected Responses cases | `LlmTarget.UpstreamModel` for Responses cases |

Chat and Responses can use different models; configuring one does not declare support for the other. Test fixtures own public aliases, target IDs, credential IDs, Session IDs and protocol flags. Additional profiles may configure different providers, servers, models and keys, and Session scenarios must verify the entire target binding.

## Volcengine speech

Use prefix `ASGARD_LOKI_TEST_VOLCENGINE_AUDIO_`:

| Suffix | Requirement | Maps to |
| --- | --- | --- |
| `API_KEY` | Required for API Key authentication | `VolcengineSpeechOptions.ApiKey` |
| `APP_ID` | Required for legacy authentication | `VolcengineSpeechOptions.AppId` |
| `ACCESS_KEY` | Required for legacy authentication | `VolcengineSpeechOptions.AccessKey` |
| `RESOURCE_ID` | Required | `VolcengineSpeechOptions.ResourceId` |
| `VOICE` | Required | `SpeechRequest.Voice`; an actual authorized speaker ID |
| `ENDPOINT` | Optional | Full `VolcengineSpeechOptions.Endpoint`; absent means the adapter's documented default |

Configure either `API_KEY` or the pair `APP_ID` + `ACCESS_KEY`. Reject mixed authentication or an incomplete legacy pair. Do not borrow an Ark LLM key, guess a speaker, or infer a resource ID. Request parameters such as text, encoding and sample rate belong in the selected test cases; credentials and authorized voice/resource choices belong in environment configuration.

## OpenAI images

Use prefix `ASGARD_LOKI_TEST_OPENAI_IMAGE_`:

| Suffix | Requirement | Maps to |
| --- | --- | --- |
| `API_KEY` | Required | `OpenAiImageOptions.ApiKey` |
| `MODEL` | Required | `OpenAiImageOptions.Model`; actual upstream image model |
| `BASE_URL` | Optional | `OpenAiImageOptions.BaseAddress`; absent means the adapter's documented default |
| `ORGANIZATION` | Optional | `OpenAiImageOptions.Organization` |
| `PROJECT` | Optional | `OpenAiImageOptions.Project` |

Size, quality, output format, transparency and streaming options belong in model-specific test cases. An environment setting does not prove the configured model supports a parameter.

## Redis integration

`ASGARD_LOKI_TEST_REDIS_CONNECTION_STRING` supplies a dedicated test Redis connection only for explicitly selected Redis integration scenarios, with `ASGARD_LOKI_TEST_LIVE=true`. It is optional for provider-only tests and must not be required by ordinary unit tests.

A Redis fixture must supply both an `IDistributedCache` implementation and real shared coordination for `ILlmSessionLock`; configuring a connection string alone does not prove distributed correctness. Use a unique key prefix per run, clean up only that prefix, never flush the database, and never log the connection string. A Redis integration fixture is not implemented yet.

## Parameter cases and evidence

Environment variables select accounts and upstream targets. Keep the parameter matrix in test data, rather than creating an environment variable for every generation option. For each provider/protocol, check supported parameters, omitted/default/explicit-null distinctions, boundaries and meaningful combinations. Assert the final HTTP request through the public Loki entry point; preserve unknown fields and reject reserved-field overrides.

Live cases must distinguish parameter transmission, upstream acceptance and observable behavior. Check actual image/audio metadata or structured-output constraints where deterministic. Parameters with statistical or perceptual effects require repeated comparison or manual review and must not rely on a fixed generated sentence. A successful login or HTTP 200 is not sufficient evidence that every parameter worked.

Report provider, capability, profile, upstream model, scenario, request count and the measured assertion. Do not print keys, authorization headers, connection strings or complete process environment dumps. Treat prompts, response snapshots and generated media as potentially sensitive. Save approved local artifacts under ignored `artifacts/`; do not commit them.

## Configure locally

Use [.env.example](../.env.example) as a naming checklist. A local `.env` is ignored by Git, but it is **not loaded automatically**. Set process environment variables explicitly or use a caller-selected loader; do not execute a dotenv file as shell code.

PowerShell example for a future Volcengine Chat scenario:

```powershell
$env:ASGARD_LOKI_TEST_LIVE = 'true'
$env:ASGARD_LOKI_TEST_VOLCENGINE_LLM_API_KEY = '<your-test-key>'
$env:ASGARD_LOKI_TEST_VOLCENGINE_LLM_BASE_URL = '<your-https-api-root-with-version-path>'
$env:ASGARD_LOKI_TEST_VOLCENGINE_LLM_CHAT_MODEL = '<your-authorized-upstream-model-or-endpoint-id>'
$env:ASGARD_LOKI_TEST_MAX_REQUESTS = '5'
```

Never put real values into this document or `.env.example`. Future manually triggered integration CI must use the same names, keep secret values in GitHub Actions Secrets, and provide only the selected fixture's configuration. Fork PRs and ordinary CI must not receive provider credentials or invoke live services. These settings do not trigger NuGet publication.
