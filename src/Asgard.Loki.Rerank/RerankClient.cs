using Asgard.Loki.Rerank.Internal;
using System.Text.Json;

namespace Asgard.Loki.Rerank;

/// <summary>独立执行重排路由与请求快照，既不持有 LLM Session 也不协调其他能力。</summary>
public sealed class RerankClient
{
    private readonly Dictionary<string, RerankRouteState> _routes = new(StringComparer.Ordinal);

    /// <summary>根据已复制的配置创建本客户端独立的路由状态；外部适配器保持借用关系。</summary>
    internal RerankClient(RerankConfiguration configuration)
        => _routes = configuration.Routes.ToDictionary(p => p.Key, p => new RerankRouteState(p.Value), StringComparer.Ordinal);

    /// <summary>选择一次完整目标并调用；取消、失败或超时后均不自动改发其他目标。</summary>
    public Task<RerankResult> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("重排扩展字段不能覆盖公共 model 或包含非 JSON 的 Undefined 值。", nameof(request));
        var snapshot = request with { Parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) };
        if (!_routes.TryGetValue(snapshot.Model, out var route)) throw new InvalidOperationException("重排模型未配置。");
        return route.Select().RerankAsync(snapshot, cancellationToken);
    }
}
