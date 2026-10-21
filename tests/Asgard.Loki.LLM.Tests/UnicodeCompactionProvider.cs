using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>只为压缩输入的公共快照边界提供原生替身，不依赖 HTTP 或实际模型。</summary>
internal sealed class UnicodeCompactionProvider : IResponsesAdapter, IResponsesCompactionAdapter
{
    private readonly StubLlmProvider _http = new();
    /// <inheritdoc />
    public string Id => "compaction";
    internal List<JsonObject> Compacted { get; } = [];
    /// <inheritdoc />
    public Task<ResponseCompaction> CompactAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    { Compacted.Add(request.DeepClone().AsObject()); return Task.FromResult(new ResponseCompaction(JsonSerializer.Deserialize<JsonElement>("{\"output\":[]}"), context.Metadata)); }
    /// <inheritdoc />
    public Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => _http.CreateAsync(request, context, cancellationToken);
    /// <inheritdoc />
    public IAsyncEnumerable<ResponseEvent> StreamAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => ((IResponsesAdapter)_http).StreamAsync(request, context, cancellationToken);
}
