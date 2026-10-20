# Asgard.Loki.Infrastructure.Tests

Offline xUnit v3 tests for the public SSE framing contract: line endings, multiple/empty data fields, inherited/reset event identity, retry parsing, strict UTF-8/BOM, incomplete events, cancellation and borrowed-stream lifetime. Provider suites independently verify their native HTTP/error/terminal semantics through the same public transport implementation.

NDJSON byte-framing cases additionally cover detached snapshots across pooled buffers, fragmented Unicode/arbitrary bytes, empty/CR records, exact record/total bounds, declared lengths, invalid policies before reads, early consumer exit and cancellation at EOF or after the last record. They do not interpret JSON or supplier lifecycle.
