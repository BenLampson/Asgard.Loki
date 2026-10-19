# Asgard.Loki.LLM.Conversion.Tests

Offline xUnit v3 conversion acceptance tests use hand-maintained fixtures, not production converters to generate expected values.

- Twelve request directions check independent native field paths for instructions, text, Base64 images, function definitions, original call IDs, JSON parameters, result links, named choice and output limits.
- Twelve complete-response directions check actual text, original function IDs, arguments, finish/usage shape and source immutability.
- Twelve stream directions check text before completion, complete tool arguments, target lifecycle, item indices, sequence numbers, final snapshots and usage.
- Twenty-four complete/stream public SDK pipeline cases use the real OpenAI, Anthropic and Google adapters with substitute HTTP handlers and fragmented SSE, verifying final endpoint/authentication/model/stream and conversion metadata.
- Regression cases cover unknown fields, explicit loss consent, native copies, immutable snapshots, signed/encrypted state, response-chain rejection, schema/effort, tool choice none, orphan/ambiguous/parallel tool calls, truncated/failed streams, inconsistent final output, timestamp mapping and changed Session conversion identity.
- Usage cases verify Claude cache-read versus uncached input and Gemini candidate versus thinking output counts, as well as OpenAI detail/zero-value preservation.
- Complete OpenAI URL citations add 64 cases: two authored full reply goldens, source/range/order preservation, ASCII prefix rebasing, Unicode ambiguity, malformed/unknown fields, original JSON, tool association, history/stream boundaries and four public HTTP/SSE cases. Converted citation streams remain explicitly unsupported; native streams retain all citation events and snapshots.
- User PDF inputs add 126 cases, including twelve independent full request comparisons and history roundtrips, distinct file-byte/order checks and 42 public SDK HTTP/SSE cases. Rejections cover malformed/missing data, MIME inference, provider references, unresolved URLs, roles and conflicting sources. Filename/unknown metadata remain explicit losses; native copies preserve references. Opaque byte fixtures and ordinary text replies do not verify renderable documents or live file/model behavior.

The tests use `MemoryDistributedCache` and explicit test coordination. They never read live credentials or contact models/Redis. Passing this suite establishes the [documented subset](../../src/Asgard.Loki.LLM.Conversion/README.md), not universal lossless conversion or model behavior.

Embedded [source and golden fixtures](Fixtures/README.md) add 36 complete structure/event comparisons across the twelve directions. Generation settings have separate twelve-direction effort/Schema assertions, 24 public SDK/HTTP settings cases, and rejection tests for budget/effort/output-intent conflicts, unsupported metadata/enforcement, zero/false/null handling, parallel switches and changed Session options. Original schema constraints and source envelopes remain independently asserted.
