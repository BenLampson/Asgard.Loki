# Asgard.Loki.Infrastructure.Tests

Offline xUnit v3 tests for the public SSE framing contract: line endings, multiple/empty data fields, inherited/reset event identity, retry parsing, strict UTF-8/BOM, incomplete events, cancellation and borrowed-stream lifetime. Provider suites independently verify their native HTTP/error/terminal semantics through the same public transport implementation.

NDJSON byte-framing cases additionally cover detached snapshots across pooled buffers, fragmented Unicode/arbitrary bytes, empty/CR records, exact record/total bounds, declared lengths, invalid policies before reads, early consumer exit and cancellation at EOF or after the last record. They do not interpret JSON or supplier lifecycle.

A non-seekable byte-fragmented source rejects Length/Position probes. Exact budgets include BOM, comments, fields and delimiters; EOF validates declared lengths. Existing strict/default decoding and SSE framing remain intact. Tests cover malformed tails, raw limit/length failures, cancellation before reading/after a frame/at EOF, source read failures, metadata-only input and borrowed lifetime on early exit. No frame or DONE-shaped data is treated as model success.

## Shared boundary ownership

| Implementation | Matrix owner | Supplier regression scope |
| --- | --- | --- |
| HttpBodyReader.ReadAsync / CopyToAsync | HttpBodyReaderTests and HttpTransferTests: bytes, bounds/declared length, EOF faults/cancellation, owned read stream and borrowed destination | Option wiring, native status/media/errors and outer HTTP/client closure |
| BorrowedStreamContent | HttpTransferTests: remaining bytes, one send, bounds, failures and borrowed input/output | Native multipart fields or JSON wrapping and one ownership integration |
| SseReader | SseReaderTests / BoundedSseReaderTests: framing, UTF-8, transport metadata, byte bounds and borrowed input | Native event/ID/tool lifecycle, actual parser mode and supplier completion rules |
| NdjsonReader | NdjsonReaderTests: raw records, snapshots, record/total bounds and borrowed input | Native record schema, identities and protocol terminal states |

Supplier-local readers, outer resource closures and native terminal policies remain tested in their owning packages even when their test shapes resemble these cases. Native protocol matrices shared by compatible profiles stay in their existing provider suites. Common Session replay/coordination/target selection belongs to LLM.Tests. No new test framework or production abstraction is introduced.
