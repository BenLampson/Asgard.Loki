namespace Asgard.Loki.Audio.Internal;

/// <summary>仅供 Audio 文件操作使用的单路由选择状态；每个客户端/操作/路由分别实例化。</summary>
internal sealed class AudioFileRouteState<T>(T[] targets)
{
    private int _sequence = -1;
    /// <summary>检查完整已配置集合，不按可用功能隐式过滤目标。</summary>
    internal bool All(Func<T, bool> predicate) => targets.All(predicate);
    /// <summary>单次选择一个完整目标，不重试或切换；并发调用独立推进本路由。</summary>
    internal T Select() => targets[(int)((uint)Interlocked.Increment(ref _sequence) % (uint)targets.Length)];
}
