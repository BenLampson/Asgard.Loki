# Image capability offline tests

96 xUnit v3 core tests reference only the entry and Image capability. No HTTP provider, Audio/LLM package, real key, network, Redis or image codec is used.

The complete authored `edit-native-request.json` / `edit-native-response.json` fixtures preserve image references, mask objects, nested/null/false settings, ordered Base64/URL items, revised prompt and native usage. They are contract examples, not OpenAI acceptance or rendering tests. Multipart tests use distinct observed streams to assert image order, separate mask role, current position, literal/repeated/empty fields, metadata snapshots and no hidden reads/seeks/disposal. Reused stream objects fail before dispatch; no upload is duplicated or rewound.

Custom adapters validate isolated generation/edit routes, per-client/per-edit-model counters, registration snapshots, no retry/failover and optional all-target streaming gates. Stream tests retain original objects/SSE identity, allow immediate unknown nonterminal events, hold terminal success until source enumeration/disposal completes and reject missing/duplicate/late/null/tail failures. A dedicated async enumerator throws from DisposeAsync after normal MoveNext completion. Cancellation/early stop leave borrowed uploads and targets open. JSON and multipart snapshots occur before enumeration begins.

Build this project in Release, then run its xUnit v3 executable or the repository offline test script after building the solution. The separate Image.OpenAI test project owns generation HTTP/SSE and native nonstream JSON/multipart editing wire coverage. Native editing SSE and live image verification remain separate future work.
