namespace Asgard.Loki.Image;

/// <summary>异步图片协议可选的显式单次查询契约，不执行后台轮询。</summary>
public interface ILokiImageTaskHandler
{
    /// <summary>查询原引用绑定的任务，返回原生及统一状态。</summary>
    Task<LokiImageGenerationResult> GetAsync(LokiImageInvocation invocation, LokiImageTaskReference reference,
        CancellationToken cancellationToken = default);
}
