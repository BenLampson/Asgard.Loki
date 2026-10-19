using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>应用原生适配器替身，异步 finally 用于检验转换对完整源枚举/释放的等待。</summary>
internal sealed class BedrockAsyncStreamProvider(bool nova) : IBedrockNovaAdapter, IBedrockConverseAdapter
{
    internal bool NativeNova => nova;
    internal bool FailDispose { get; init; }
    internal Action? OnDispose { get; init; }
    internal bool Disposed { get; private set; }
    internal int Calls { get; private set; }
    internal List<LlmJsonEvent> Created { get; } = [];
    /// <inheritdoc />
    public string Id => "native-stub";
    /// <inheritdoc />
    public string BindingIdentity => "native-stub-v1";
    /// <inheritdoc />
    public Task<BedrockNovaResponse> InvokeAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <inheritdoc />
    public Task<BedrockConverseResponse> ConverseAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>只构造完整原生逻辑事件，不持有 HTTP、Redis 或跨会话状态。</summary>
    async IAsyncEnumerable<BedrockNovaEvent> IBedrockNovaAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var row in Rows(token))
        {
            var item = new BedrockNovaEvent(JsonSerializer.SerializeToElement(row["data"]), context.Metadata,
                row["eventName"]!.GetValue<string>(), row["eventType"]!.GetValue<string>()); Created.Add(item); yield return item;
        }
    }
    /// <summary>Converse 事件名独立于载荷，不能从工具参数或 type 字段推断。</summary>
    async IAsyncEnumerable<BedrockConverseEvent> IBedrockConverseAdapter.StreamAsync(JsonObject request, LlmAdapterContext context, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var row in Rows(token))
        {
            var item = new BedrockConverseEvent(JsonSerializer.SerializeToElement(row["data"]), context.Metadata, row["eventName"]!.GetValue<string>());
            Created.Add(item); yield return item;
        }
    }
    /// <summary>源主动交付逻辑停止后仍有异步释放步骤；转换层不能提前宣告整次成功。</summary>
    private async IAsyncEnumerable<JsonObject> Rows([EnumeratorCancellation] CancellationToken token)
    {
        Calls++;
        try
        {
            foreach (var row in BedrockStreamFixtures.Rows(nova)) { token.ThrowIfCancellationRequested(); await Task.Yield(); yield return row; }
        }
        finally
        {
            await Task.Yield(); Disposed = true; OnDispose?.Invoke(); if (FailDispose) throw new IOException("async source dispose");
        }
    }
}
