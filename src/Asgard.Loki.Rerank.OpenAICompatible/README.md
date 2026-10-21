# Asgard.Loki.Rerank.OpenAICompatible

An explicit native `model/query/documents` to `results/index/relevance_score` HTTP wire for caller-selected compatible services. Configure the entire target; no default hosted endpoint or service entitlement is implied. The package references only Rerank, capability-independent Infrastructure and standard HTTP support. Other suppliers retain their own validation contracts.

## Support matrix

| Area | Native contract |
| --- | --- |
| Operation | One JSON POST rerank under an explicit API root |
| Request | Configured actual model, native string query, nonempty original JSON documents array |
| Options | Nullable positive exact top_n, bool/null return_documents, original native settings and unknown JSON |
| Result | Original object/results/index/relevance_score/document/usage and unknown fields; identity/usage need not exist |
| Association | Complete min(top_n,input count) results, exact unique input indices, original JSON/text echo verification |
| Scores | Finite native numbers, including negative or greater-than-one values; no sorting/normalization |
| Authentication | Explicit Anonymous or BearerToken mode, independent Id/model/key/root/named client |
| Limits/lifetime | Positive 32/64 MiB request/response defaults, raw error bytes/type/status, EOF/disposal/final cancellation before publication |
| Offline/live | tests with independent complete fixtures; live service/model/relevance acceptance remains unverified |

## Initialization

```csharp
using Asgard.Loki;
using Asgard.Loki.Rerank;
using Asgard.Loki.Rerank.OpenAICompatible;
using System.Text.Json;

var target = new OpenAiCompatibleRerankAdapter(factory, new OpenAiCompatibleRerankOptions
{
    Id = "compatible-ranker",
    BaseAddress = callerSelectedApiRoot,
    Model = callerSelectedActualModel,
    Authentication = OpenAiCompatibleRerankAuthentication.BearerToken,
    ApiKey = callerSelectedToken
});
await using var loki = new LokiClientBuilder().AddRerank(new RerankOptions
{
    Routes = [new RerankRoute { Model = "ranker", Targets = [target] }]
}).Build();
var result = await loki.Rerank.RerankAsync(new RerankRequest
{
    Model = "ranker",
    Parameters = new()
    {
        ["query"] = JsonSerializer.SerializeToElement("search query"),
        ["documents"] = JsonSerializer.SerializeToElement(new[] { "first", "second" }),
        ["top_n"] = JsonSerializer.SerializeToElement(1),
        ["return_documents"] = JsonSerializer.SerializeToElement(false)
    }
}, cancellationToken);
foreach (var item in result.OpenAiCompatibleRankings)
    Console.WriteLine($"Input {item.Index}: {item.RelevanceScore}");
```

The factory, API root, actual model, token and cancellation token are caller-owned. API roots must be absolute HTTP(S) without URI credentials, query or fragment. The adapter appends only rerank, preserving version/proxy prefixes with or without a trailing slash. No hostname/model-prefix classification, provider discovery or model catalog is used. Public route Model maps to the configured native model; native returned model/identity fields remain untouched in Data. The default HTTP client name is Asgard.Loki.Rerank.OpenAICompatible. Use explicit Anonymous with no ApiKey when the selected service requires no authentication. Credentials are excluded from options JSON; both modes remove unrelated factory authentication/organization/project defaults.

## Fidelity and limits

Original candidate values may be text or opaque native JSON; this wire does not certify a model's acceptance of a particular candidate type. Query remains a string. Empty/whitespace text, arrays/objects/null candidates, original order, unknown nested fields, null/false values and exact JSON numbers are retained. No document flattening, text rendering, tokenization, truncation, media fetching or candidate filtering occurs. Native options such as max_chunk_per_doc or overlap_tokens pass unchanged; their acceptance/meaning belongs to the selected upstream.

Absent/null top_n expects all candidates; explicit positive top_n caps the expected result count without changing the parameter or allocating by its value. return_documents true requires the document field; absent/null/false does not request inferred echoes. A supplied echo must match the indexed original JSON, a native text wrapper containing that original value, or an object preserving every original object field. Extra native echo fields stay raw. An explicit null echo matches an original null candidate, while missing remains distinct. The SDK does not guess a rendering of an opaque candidate or fabricate an omitted document.

Results must be a complete native results array with unique exact indices belonging to the input set. Empty/incomplete/duplicate/foreign associations fail. RelevanceScore is a double convenience projection; raw precision remains in item.Data and result.Data. No probability range, score recalculation, sorting, synthetic index, mandatory returned model/id or estimated usage is added. Missing usage stays absent; explicit null stays null. When supplied, usage must be an object and known token counters exact nonnegative integers; unknown fields remain untouched without summing or billing calculations. Separate SGLang root score arrays, arbitrary envelopes or alternative score names fail explicitly.

MaxRequestBytes defaults to 32 MiB and MaxResponseBytes to 64 MiB, including errors. Full snapshots precede HTTP client creation. Strict Unicode/unique JSON member checks include unknown fields. Provided successful content type must be application/json with optional UTF-8 charset; malformed raw types fail, while HTTP errors retain original type values. Declared length, byte-limit, actual target/method, late read/disposal failures and final cancellation are checked before success. SSE is rejected before reading a successful stream.

RerankUpstreamException retains actual StatusCode, copied Body bytes and ContentType for HTTP and native HTTP200 errors. ResponseBody is a strict UTF-8 projection for byte errors; read Body for invalid binary text. Per-call streams/responses/requests/clients are disposed before publication; factories/adapters remain borrowed. There is no retry/failover, server hosting, credential acquisition, gateway route management, shared cross-capability coordinator, vector database, RAG, background processing or billing feature. All protocol verification is offline; actual service/model acceptance, ranking quality and opaque-content interpretation remain unverified.
