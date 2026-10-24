# Asgard.Loki.LLM.DeepSeek

For model-generation JSON mode/Schema support, use the existing [OpenAI-compatible generation adapter](../Asgard.Loki.LLM.OpenAI/README.md#explicit-deepseek-native-endpoints) or separate Messages adapter and their explicit targets. The [format matrix and call examples](../../docs/llm-output-formats.md) distinguish DeepSeek Chat JSON mode, Responses Schema and unsupported Messages formatting. Search/Fetch in this package has no model output-format declaration.

Native DeepSeek Search and Fetch JSON calls for the LLM supplier package. `DeepSeekToolsClient` captures one service root and explicit API Key, and borrows `IHttpClientFactory`. The native tools do not require a model or conversation, so they use a supplier-specific client and result without adding a model selector, Session or another capability module. Model-generation adapters and protocol conversion are separate.

## Support matrix

| Operation | Native endpoint relative to the tools root | Request / result |
| --- | --- | --- |
| `SearchAsync` | `POST search` | Original query/options; complete ranked `results` and metadata, including an empty array |
| `FetchAsync` | `POST fetch` | Original page URL; complete `title` / Markdown `content`, including empty strings |
| Authentication | Selected `Authorization: Bearer` | Explicit fixed Key, independent of other clients |
| Streaming | Unsupported | Complete JSON only; unexpected SSE fails before body reads |

The default root is `https://api.deepseek.com/v1/tools/`. Set `BaseAddress` explicitly for a proxy prefix or another endpoint implementing this contract. HTTP(S), prefixes and explicit ports are preserved. Only the two method-owned relative paths are appended. A Fetch `url` is sent in JSON to this fixed service; the SDK never visits it, fetches returned links/icons, executes content or automatically follows Search with Fetch/model calls.

## Initialize

```csharp
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.DeepSeek;

// Borrow application-owned httpFactory; supply this client's Key explicitly.
var tools = new DeepSeekToolsClient(httpFactory, new DeepSeekToolsOptions
{
    ApiKey = apiKey
});
var search = await tools.SearchAsync(new JsonObject
{
    ["query"] = "site:example.com native query",
    ["count"] = 3,
    ["need_content"] = true
}, cancellationToken);

// A separate explicit operation, chosen by the application.
var page = await tools.FetchAsync(new JsonObject
{
    ["url"] = "https://example.com/article"
}, cancellationToken);
// search.Data / page.Data retain the entire native JSON.
```

No `LokiClientBuilder` registration or model route is needed for these stateless auxiliary APIs. The provider package references only LLM, Infrastructure and standard HTTP support. It adds no unified search/tool-execution abstraction or capability-to-capability dependency. Application code owns any generation, retrieval workflow and tool-call association.

## Native fields and fidelity

Requests are original `JsonObject` parameters, deeply snapshotted before asynchronous transport. `query` and a non-null `ori_question` allow 1–128 Unicode scalar values and cannot be all whitespace; whitespace/content are retained. Optional `count` is an integer from 1–50, `localization` is a string and `need_content` is a Boolean. Omitted defaults and explicit `ori_question: null` remain unchanged. No IP discovery, query rewriting, prompt template, model suffix rule or default-field injection occurs.

Fetch accepts a nonempty HTTP(S) URL with a host and no user information, at most 8192 Unicode scalar values. Raw controls and surrounding whitespace fail instead of being silently removed by a URI parser. The original URL string, query and fragment remain in the JSON body. Unknown native fields remain unchanged, including business `model` fields; root `stream` controls are refused because these methods expose only complete JSON.

Search retains original order, duplicate URLs, native score values, authority, titles, snippets, summaries, content, site/date metadata and future fields. Nullable metadata absence stays absent; null stays null. Core result fields have native type validation; score values must be finite and authority integral, with no quality thresholds, clamping, reranking or date normalization. Fetch retains title/content without parsing Markdown. Empty native results/content remain valid exchanges. `DeepSeekToolsResult` independently owns `Data` and exposes the method-owned `Operation`, fixed `Endpoint`, actual `StatusCode` and original `ContentType`; it invents no model, token usage, response ID or completion state.

See the primary [Search reference](https://api-docs.deepseek.com/api/search/) and [Fetch reference](https://api-docs.deepseek.com/api/fetch/) for provider fields and acceptance. Endpoint/account availability, search quality and page completeness remain upstream concerns.

## Limits and transport ownership

`MaximumRequestBytes` defaults to 1 MiB and bounds final UTF-8 JSON before creating a client. `MaximumResponseBytes` defaults to 16 MiB and bounds raw success/error bodies. Both must be positive. Strict UTF-8/Unicode and duplicate-property checks include unknown fields. Successful bodies must be complete JSON objects with operation-specific native structure; missing media type is accepted only with valid native JSON, otherwise JSON/`+json` and UTF-8 are required.

The provider checks actual method/target (including in-place request changes), declared length, total bytes, normal EOF, asynchronous source disposal, response/client disposal and final cancellation before result delivery. HTTP errors and root native errors at HTTP 200 use `LlmUpstreamException` with original bytes, content type and actual status, including binary errors. No body truncation, retry, backoff, credential fallback or target switching occurs. A later explicit operation is a new single request.

The fixed Key overrides a same-kind named-client Authorization default; conflicting `api-key`, `x-api-key` or `x-goog-api-key` defaults fail before send. The Key is excluded from `System.Text.Json` configuration serialization and default object display. Configure the application-owned named client `Asgard.Loki.LLM.DeepSeek.Tools` (or explicit `HttpClientName`) with suitable deadlines and automatic redirects/retries/hedging disabled. Pass an operation-deadline cancellation token to bound the entire body read; named-client header timeouts alone do not cover it. Hidden handler behavior remains application-owned. The factory/handler are borrowed; each operation owns and closes its client, message, response and body stream.

## Verification status

Tests use independently authored complete protocol windows, fake HTTP and fragmented nonseekable streams. They compare full requests/results for both operations, minimal/default omission, original text/URL/metadata/exact numbers, empty outcomes and unchanged ranking/duplicates. Boundaries cover Unicode scalar and byte limits, malformed native input/output, strict encoding/duplicates/media/length, HTTP200/native/binary errors, method/target drift, input mutation during waits, final resource/cancellation failures, credential/default-header isolation and concurrent fixed-target clients. No real Key, network/page fetch, Redis or model call is used. Live DeepSeek acceptance, page/search quality and real credential validation have not been performed.
