using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>同一可见 ID 分别模拟原生与显式转换结果，核心不能靠字符串前缀判断续传能力。</summary>
internal sealed class ResponseOwnershipProvider(LlmProtocol source) : IResponsesAdapter
{
    /// <inheritdoc />
    public string Id => "ownership";
    /// <summary>验证非法续接没有再进入适配器。</summary>
    internal int Calls { get; private set; }
    /// <summary>保留可见响应 JSON，同时只通过现有元数据声明转换来源。</summary>
    public Task<Response> CreateAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        var data = JsonSerializer.SerializeToElement(new { id = "loki_same_visible_id", @object = "response", status = "completed", output = new object[0] });
        var metadata = source == LlmProtocol.None ? context.Metadata : context.Metadata with
            { Conversion = new(source, LlmProtocol.Responses, JsonSerializer.SerializeToElement(new { native = true }), []) };
        return Task.FromResult(new Response(data, metadata));
    }
    /// <summary>真实枚举 created/completed，未知事件中的同名对象仍不具有原生生命周期身份。</summary>
    public async IAsyncEnumerable<ResponseEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await CreateAsync(request, context, cancellationToken);
        foreach (var type in new[] { "response.created", "response.completed" })
            yield return new(JsonSerializer.SerializeToElement(new { type, response = response.Data }), response.Metadata);
    }
}
