using Asgard.Loki.Abstractions;

namespace Asgard.Loki;

/// <summary>统一持有能力模块，并管理由模块工厂创建的资源。</summary>
public sealed class LokiClient : ILokiClient
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, Lazy<object>> _modules;
    private Task? _disposeTask;

    /// <summary>对注册快照建立延迟初始化模块。</summary>
    internal LokiClient(IReadOnlyDictionary<Type, Func<object>> factories)
    {
        _modules = factories.ToDictionary(pair => pair.Key,
            pair => new Lazy<object>(pair.Value, LazyThreadSafetyMode.ExecutionAndPublication));
    }

    /// <inheritdoc />
    public T GetModule<T>() where T : class
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            return _modules.TryGetValue(typeof(T), out var module)
                ? (T)module.Value
                : throw new InvalidOperationException($"模块 {typeof(T).Name} 未注册。");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            // 先发布释放状态，再运行释放回调，防止回调重新创建模块。
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            var created = _modules.Values.Where(module => module.IsValueCreated)
                .Select(module => module.Value).Distinct(ReferenceEqualityComparer.Instance).Reverse().ToArray();
            _ = DisposeModulesAsync(created, completion);
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>逐个释放已创建模块，并保留全部释放错误。</summary>
    private static async Task DisposeModulesAsync(object[] modules, TaskCompletionSource completion)
    {
        List<Exception> errors = [];
        foreach (var module in modules)
        {
            try
            {
                if (module is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync().ConfigureAwait(false);
                else if (module is IDisposable synchronous) synchronous.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count == 0) completion.SetResult();
        else completion.SetException(new AggregateException(errors));
    }
}
