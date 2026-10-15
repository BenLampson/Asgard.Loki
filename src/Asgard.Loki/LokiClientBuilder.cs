using Asgard.Loki.Abstractions;

namespace Asgard.Loki;

/// <summary>构建不依赖具体能力包的统一客户端。</summary>
public sealed class LokiClientBuilder : ILokiClientBuilder
{
    private readonly Dictionary<Type, Func<object>> _factories = [];

    /// <inheritdoc />
    public ILokiClientBuilder AddModule<T>(Func<T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryAdd(typeof(T), () => factory()
            ?? throw new InvalidOperationException($"模块 {typeof(T).Name} 的工厂返回了空值。")))
        {
            throw new InvalidOperationException($"模块 {typeof(T).Name} 已注册。");
        }

        return this;
    }

    /// <inheritdoc />
    public ILokiClient Build() => new LokiClient(_factories);
}
