# Asgard.Loki.LLM.NewAPI

Calls an explicitly configured existing New API/Sub2API upstream's native API-Key `alpha/search` operation. This pure .NET SDK package references only LLM, Infrastructure and standard HTTP factory support. Chat/Responses/compact/Messages/Gemini use existing native provider packages; this client adds the distinct API-Key search contract.

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

Provide a complete API root such as a caller-selected `/v1/` or proxy prefix. Only `alpha/search` is appended. The target owns `model`; Parameters must omit model and its case aliases. No default model, host detection, key parsing or credential discovery occurs. Native query/id/stream and all unknown fields remain original, including omitted/null/false/true values. Setting stream does not switch this single complete-window method to an SSE parser. No search result schema, completion state, usage or citation quality is inferred from the bytes.

Options and caller parameters are frozen before factory creation. Complete body limits default to 1 MiB request and 32 MiB success/error response. Strict request Unicode/duplicate-field checks, byte/declared-length limits, actual EOF, owned-resource closure and final cancellation precede delivery. Non-2xx errors use LlmUpstreamException with original status/body bytes/content type. A 2xx business-error-shaped body remains original data. No retry, target switch, model Session/history, polling, local search, URL/media fetching, tool execution, gateway administration or billing is performed. Factory is borrowed; per-call HTTP resources are owned. Disable retries/hedging in the application-owned HTTP pipeline and update client identity/version when its semantic configuration changes. This API-Key client does not borrow Codex OAuth accounts or token sources.

Validation: 81 passing offline xUnit v3 cases cover independent New API/Sub2API/proxy vectors, raw HTTP windows, strict input and resource/byte/length/final-cancellation boundaries. This does not establish real service/model acceptance or parameter effectiveness.
