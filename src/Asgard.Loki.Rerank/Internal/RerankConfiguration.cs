namespace Asgard.Loki.Rerank.Internal;

/// <summary>注册时验证并复制原生目标集合，多个客户端只共享不可变配置而不共享轮询状态。</summary>
internal sealed class RerankConfiguration
{
    /// <summary>复制调用者的路由集合与目标数组，不取得适配器的释放权。</summary>
    internal RerankConfiguration(RerankOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.Routes);
        var routes = new Dictionary<string, IRerankAdapter[]>(StringComparer.Ordinal);
        foreach (var route in options.Routes)
        {
            ArgumentNullException.ThrowIfNull(route); ArgumentException.ThrowIfNullOrWhiteSpace(route.Model);
            ArgumentNullException.ThrowIfNull(route.Targets);
            var targets = route.Targets.ToArray(); ArgumentOutOfRangeException.ThrowIfZero(targets.Length);
            foreach (var target in targets)
            {
                ArgumentNullException.ThrowIfNull(target); ArgumentException.ThrowIfNullOrWhiteSpace(target.Id);
            }
            if (!routes.TryAdd(route.Model, targets)) throw new ArgumentException("重排模型路由重复。", nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfZero(routes.Count); Routes = routes;
    }

    /// <summary>只在模块内部读取，数组不对公共调用者暴露，也不由客户端修改。</summary>
    internal IReadOnlyDictionary<string, IRerankAdapter[]> Routes { get; }
}
