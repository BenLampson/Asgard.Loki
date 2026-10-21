using Asgard.Loki.Embedding.Internal;
using System.Text.Json;

namespace Asgard.Loki.Embedding;

/// <summary>独立执行嵌入路由与请求快照，既不持有 LLM Session 也不协调其他能力。</summary>
public sealed class EmbeddingClient
{
    private readonly Dictionary<string, EmbeddingRouteState> _routes = new(StringComparer.Ordinal);

    /// <summary>根据已复制的配置创建本客户端独立的路由状态；外部适配器保持借用关系。</summary>
    internal EmbeddingClient(EmbeddingConfiguration configuration)
        => _routes = configuration.Routes.ToDictionary(p => p.Key, p => new EmbeddingRouteState(p.Value), StringComparer.Ordinal);

    /// <summary>选择一次完整目标并调用；取消、失败或超时后均不自动改发其他目标。</summary>
    public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("嵌入扩展字段不能覆盖公共 model 或包含非 JSON 的 Undefined 值。", nameof(request));
        var snapshot = request with { Parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) };
        if (!_routes.TryGetValue(snapshot.Model, out var route)) throw new InvalidOperationException("嵌入模型未配置。");
        return route.Select().EmbedAsync(snapshot, cancellationToken);
    }
}
