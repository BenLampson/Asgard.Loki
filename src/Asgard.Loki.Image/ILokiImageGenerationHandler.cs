namespace Asgard.Loki.Image;

/// <summary>可作为单例复用的图片协议处理器，映射与原生入口由各供应商实现。</summary>
public interface ILokiImageGenerationHandler
{
    /// <summary>协议实现名称，不用于品牌或模型推断。</summary>
    string Name { get; }
    /// <summary>影响原生操作和任务解释的稳定配置身份；自定义有配置 Handler 应覆盖。</summary>
    string BindingIdentity => Name;
    /// <summary>校验和映射统一请求并执行一次生成调用。</summary>
    Task<LokiImageGenerationResult> GenerateAsync(LokiImageInvocation invocation, LokiImageGenerationRequest request,
        CancellationToken cancellationToken = default);
    /// <summary>在显式目标执行一次原生调用，不应用统一参数支持表。</summary>
    Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default);
}
