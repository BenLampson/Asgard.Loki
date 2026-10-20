# Asgard.Loki.Infrastructure

Low-level transport primitives shared by Loki provider packages. This package depends only on .NET and contains no model requests, results, routing, credentials, tool execution or capability lifecycle.

`SseReader.ReadAsync(Stream, cancellationToken, strictUtf8)` reads complete Server-Sent Events as immutable `SseFrame` records. Frames retain multi-line data, event name, event ID and retry hints. A retry hint is data only and never reconnects or repeats a request. The parser rejects a truncated data event at EOF and leaves the borrowed stream open.

Strict UTF-8 mode rejects malformed bytes and recognizes an initial UTF-8 BOM; default decoding preserves the existing permissive StreamReader behavior. Applications/providers own byte limits, HTTP lifetime, cancellation deadlines, JSON parsing and native success/error/terminal interpretation. A parsed frame or normal parser completion does not prove model success.

```csharp
await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken, strictUtf8: true))
{
    // Interpret frame.Data using the selected provider's native protocol.
}
```

Provider packages reference this assembly directly. They do not compile linked copies of these types. The unified entry still references only Abstractions, which remains limited to module contracts. See [architecture](../../docs/architecture.md).

`HttpBodyReader.ReadAsync(HttpContent, maximumBytes, cancellationToken)` reads a complete byte body within an explicit positive limit. It preserves arbitrary binary data and validates declared Content-Length against the bytes actually read. It disposes the content's read stream before returning and propagates late read/disposal/cancellation failures. The `HttpContent` and outer response/client remain caller-owned; providers must complete outer resource disposal and cancellation checks before reporting success. This reader parses no JSON, credentials, native error or task state and never retries.
