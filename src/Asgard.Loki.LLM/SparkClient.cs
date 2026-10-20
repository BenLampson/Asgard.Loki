using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>先复制完整原生请求，再经既有协调契约绑定目标；只映射原生 domain。</summary>
internal sealed class SparkClient(LlmInvocation invocation) : ISparkClient
{
    /// <inheritdoc />
    public async Task<SparkCompletion> CompleteAsync(SparkRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Spark, options, null, cancellationToken).ConfigureAwait(false);
        payload["parameter"]!["chat"]!["domain"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        var result = await ((ISparkAdapter)call.Provider).CompleteAsync(payload, call.Context, cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); return result;
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<SparkEvent> StreamAsync(SparkRequest request, LlmCallOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var payload = Snapshot(request);
        var call = await invocation.ResolveAsync(request.Model, LlmProtocol.Spark, options, null, cancellationToken).ConfigureAwait(false);
        payload["parameter"]!["chat"]!["domain"] = call.Context.Target.UpstreamModel; cancellationToken.ThrowIfCancellationRequested();
        await foreach (var item in ((ISparkAdapter)call.Provider).StreamAsync(payload, call.Context, cancellationToken).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        cancellationToken.ThrowIfCancellationRequested();
    }
    /// <summary>第一次缓存等待前冻结全部原生参数；不写入 model/stream、AppId、默认消息或生成设置。</summary>
    private static JsonObject Snapshot(SparkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Parameters);
        foreach (var property in request.Parameters)
            if (property.Value.ValueKind == JsonValueKind.Undefined || property.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || property.Key.Equals("stream", StringComparison.OrdinalIgnoreCase)
                || new[] { "header", "parameter", "payload" }.Any(name => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Key != name)) throw new ArgumentException("星火原生参数含 SDK 控制字段覆盖、错误大小写或 Undefined。");
        LlmJsonInput.Request(request.Model, null, request.Parameters); var payload = JsonSerializer.SerializeToNode(request.Parameters)!.AsObject();
        if (payload.TryGetPropertyValue("header", out var header) && (header is not JsonObject obj || obj.Any(p => p.Key.Equals("app_id", StringComparison.OrdinalIgnoreCase)))) throw new ArgumentException("星火 header 必须为对象，app_id 由供应商配置拥有。");
        var parameter = Object(payload, "parameter");
        if (parameter.Any(p => p.Key.Equals("chat", StringComparison.OrdinalIgnoreCase) && p.Key != "chat")) throw new ArgumentException("星火原生 chat 字段大小写无效。");
        var chat = Object(parameter, "chat");
        if (chat.Any(p => p.Key.Equals("domain", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("星火 domain 只能来自已绑定实际模型，不能通过参数覆盖。");
        return payload;
    }
    /// <summary>只建立拥有模型路由意义的原生路径；已有明确 null/其他类型不能被静默覆盖。</summary>
    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent.TryGetPropertyValue(name, out var value)) return value as JsonObject ?? throw new ArgumentException("星火原生路由路径必须为对象。");
        var created = new JsonObject(); parent.Add(name, created); return created;
    }
}
