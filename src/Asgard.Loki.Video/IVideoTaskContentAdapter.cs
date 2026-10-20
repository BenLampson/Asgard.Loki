namespace Asgard.Loki.Video;

/// <summary>可选的原生任务内容读取能力，只在调用者明确请求时复制字节，不做预先查询或媒体转换。</summary>
public interface IVideoTaskContentAdapter : IVideoTaskAdapter
{
    /// <summary>将一次完整原生响应复制到借用目标流，返回前完成 HTTP 释放和最终取消检查。</summary>
    /// <remarks>不得关闭、回卷、清空或隐式刷新目标流。失败时可能已有部分字节写入，由调用者决定如何处理。</remarks>
    Task<VideoContentResult> CopyContentAsync(string taskId, VideoContentRequest request, Stream destination, CancellationToken cancellationToken = default);
}
