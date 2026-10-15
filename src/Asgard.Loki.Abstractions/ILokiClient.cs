namespace Asgard.Loki.Abstractions;

/// <summary>提供已注册能力模块的统一访问入口。</summary>
public interface ILokiClient : IAsyncDisposable
{
    /// <summary>获取指定能力模块；未注册或客户端已释放时明确失败。</summary>
    T GetModule<T>() where T : class;
}
