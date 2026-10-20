# Asgard.Loki.Infrastructure.Tests

Offline xUnit v3 tests for the public SSE framing contract: line endings, multiple/empty data fields, inherited/reset event identity, retry parsing, strict UTF-8/BOM, incomplete events, cancellation and borrowed-stream lifetime. Provider suites independently verify their native HTTP/error/terminal semantics through the same public transport implementation.

NDJSON byte-framing cases additionally cover detached snapshots across pooled buffers, fragmented Unicode/arbitrary bytes, empty/CR records, exact record/total bounds, declared lengths, invalid policies before reads, early consumer exit and cancellation at EOF or after the last record. They do not interpret JSON or supplier lifecycle.

The suite now executes **98 passing offline cases**, including 20 additional public ReadBoundedAsync cases. A non-seekable byte-fragmented source rejects Length/Position probes. Exact budgets include BOM, comments, fields and delimiters; EOF validates declared lengths. Existing strict/default decoding and SSE framing remain intact. Tests cover malformed tails, raw limit/length failures, cancellation before reading/after a frame/at EOF, source read failures, metadata-only input and borrowed lifetime on early exit. No frame or DONE-shaped data is treated as model success.
