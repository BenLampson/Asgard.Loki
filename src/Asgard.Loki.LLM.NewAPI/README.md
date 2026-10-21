# Asgard.Loki.LLM.NewAPI

Calls an explicitly configured existing New API/Sub2API upstream's native `alpha/search` operation with default API-Key authentication. This pure .NET SDK package references only LLM, Infrastructure and standard HTTP factory support. Chat/Responses/compact/Messages/Gemini use existing native provider packages; this client adds the distinct API-Key search contract.

| Native operation | Support | Live status |
| --- | --- | --- |
| POST alpha/search | One complete original HTTP result; explicit root/model/Key | Not tested with a real service/model/Key |
| Native request | Fixed selected model, caller id/stream/query/unknown values | No parameter-effect or query validity claim |
| Native response | Actual status, raw Content-Type and independent original bytes | JSON/text/binary/empty/SSE bodies remain opaque |

```csharp
using Asgard.Loki.LLM;
using Asgard.Loki.LLM.NewAPI;
using System.Text.Json.Nodes;

// factory, selectedApiRoot, selectedActualModel and selectedApiKey are caller-owned.
var client = new NewApiSearchClient(factory, new NewApiSearchOptions
{
    BaseAddress = selectedApiRoot, UpstreamModel = selectedActualModel,
    TargetId = "selected-search", Version = "caller-context-v1",
    HttpClientName = "Explicit.NewAPI.Search"
}, new LlmCredential("search-key", selectedApiKey));
var result = await client.SearchAsync(new JsonObject
{
    ["query"] = "Caller-supplied native input", ["stream"] = false
}, cancellationToken);
// result.Body is complete original bytes; result.StatusCode is an HTTP status.
```

Provide a complete API root such as a caller-selected `/v1/` or proxy prefix. By default alpha/search is appended; explicit transport configuration below can select a different native operation path. The target owns `model`; Parameters must omit model and its case aliases. No default model, host detection, key parsing or credential discovery occurs. Native query/id/stream and all unknown fields remain original, including omitted/null/false/true values. Setting stream does not switch this single complete-window method to an SSE parser. No search result schema, completion state, usage or citation quality is inferred from the bytes.

Options and caller parameters are frozen before factory creation. Complete body limits default to 1 MiB request and 32 MiB success/error response. Strict request Unicode/duplicate-field checks, byte/declared-length limits, actual EOF, owned-resource closure and final cancellation precede delivery. Non-2xx errors use LlmUpstreamException with original status/body bytes/content type. A 2xx business-error-shaped body remains original data. No retry, target switch, model Session/history, polling, local search, URL/media fetching, tool execution, gateway administration or billing is performed. Factory is borrowed; per-call HTTP resources are owned. Disable retries/hedging in the application-owned HTTP pipeline and update client identity/version when its semantic configuration changes. This client does not borrow Codex OAuth accounts or token sources.

Validation: 133 passing offline xUnit v3 cases cover independent New API/Sub2API/proxy vectors, raw HTTP windows, strict input and resource/byte/length/final-cancellation boundaries. This does not establish real service/model acceptance or parameter effectiveness.

## Other native upstream protocols

Ordinary generation calls reuse explicit [OpenAI Chat/Responses/text/compact](../Asgard.Loki.LLM.OpenAI/README.md#explicit-native-upstream-chat-responses-text-and-compact), [Messages](../Asgard.Loki.LLM.Anthropic/README.md#explicit-native-upstream-messages) and [Gemini](../Asgard.Loki.LLM.Google/README.md#native-http-and-ending-policies) provider configurations. They select their own complete root/model/Key/protocol/implementation identity and retain native field/ending semantics. This package's API-Key search client does not coordinate or convert those calls. Offline fixtures verify compatibility; actual deployment support remains caller-selected and has not been live-tested.

## Explicit search endpoint and authentication

NewApiSearchOptions.Authentication is one explicit Bearer/None/Header/Query choice. Optional NewApiSearchTransportOptions supplies a fixed relative SearchPath, fixed queries and non-secret credential name/prefix/suffix. The same client remains a single native opaque HTTP operation with a caller-selected actual model; it does not define incoming routes, parse search results or create model Sessions.

| Mode / operation | Support | Limits / live status |
| --- | --- | --- |
| Native configured POST | Full original parameters plus target-owned model, complete status/media/body | JSON/text/binary/empty/SSE/2xx business errors remain opaque; no real-service validation |
| Bearer (default) | API-Key identity and Authorization: Bearer | Original constructor/default path/body/media behavior preserved |
| Header | API-Key identity, explicit CredentialName and prefix + Key + suffix | Non-structural header name, printable ASCII value |
| Query | API-Key identity, name and formatted value escaped once | A conflicting encoded/case-variant fixed credential parameter is rejected |
| None | Explicit LlmCredential.Anonymous | No missing-Key or external-account fallback; Transport may be omitted for alpha/search |

```csharp
var client = new NewApiSearchClient(factory, new NewApiSearchOptions
{
    BaseAddress = selectedCompleteRoot, UpstreamModel = selectedActualModel,
    TargetId = "native-search", Version = "deployment-context-v1",
    HttpClientName = "Explicit.Native.Search",
    Authentication = NewApiSearchAuthentication.Query,
    Transport = new NewApiSearchTransportOptions
    {
        SearchPath = "native/search.run?existing=1", CredentialName = "api_key"
    }
}, new LlmCredential("selected-search-key", selectedApiKey));
var result = await client.SearchAsync(parameters, cancellationToken);
```

Relative paths append beneath the complete API root/proxy prefix; one leading slash has the same append semantics. Fixed queries retain original order/values and repeated business parameters, including literal URL values. Absolute/scheme-relative destinations, fragments, templates, traversal, malformed percent/UTF-8 escapes and control characters are rejected. Query names/credential values are escaped once. Keep actual credentials in LlmCredential; prefix/suffix contain non-secret formatting only. Header/Query require a name; Bearer/None reject unused formatting. For None, explicitly select it and provide LlmCredential.Anonymous("selected-search-key").

Configured calls freeze the complete endpoint/body/Host/auth before factory creation. Selected same-name defaults are overridden on the request; unselected authentication/signature/Cookie defaults fail without changing factory settings. Actual method/URI/Host/auth/content are verified before send and after owned resource closure/final cancellation. Native request Content-Type remains application/json without an injected charset. Default null Transport/Bearer options and metadata extensions remain omitted from JSON; original default calls keep their prior contract.

The client freezes root/model/credential/operation/auth/client/version/budgets at construction. Metadata adds non-secret Authentication and Transport snapshots only for explicit configuration; it still contains no Key value. Result.Endpoint is the configured operation address with fixed business queries, **excluding the SDK-injected authentication query**. The actual outgoing request carries that credential when Query is selected. Response.Body remains untouched even if an upstream includes business/error-shaped or unknown data; no native result bytes are redacted or decoded by this client.

Existing request/response byte bounds, valid Content-Length, actual EOF, all owned closures and final cancellation remain required before delivery. Raw non-2xx status/media/bytes remain in LlmUpstreamException. No retry, retargeting, SSE terminal interpretation, usage calculation, automatic polling, model Session/history, local search, tool execution or gateway routing is added. Factory remains borrowed. Disable retries, redirects, hedging and hidden authentication changes in the caller's HTTP pipeline; update version/client identity when its semantic configuration changes.

58 additional offline cases cover four explicit auth modes, whole native parameters and safe returned metadata/operation address, opaque response variants, raw errors, fixed queries/escaping, invalid config/kinds/default credentials, actual request mutations, snapshots/repeated calls and late read/content-close/final-cancellation failures. Current supplier total: 133 passing offline cases. Real service/model acceptance, credential entitlement and optional parameter effectiveness remain unverified.
