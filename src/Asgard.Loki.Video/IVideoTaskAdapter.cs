namespace Asgard.Loki.Video;

/// <summary>一个完整视频上游目标的原生任务操作；服务器、模型、凭据及操作不可隐式切换。</summary>
public interface IVideoTaskAdapter
{
    /// <summary>路由内唯一的完整目标标识。</summary>
    string Id { get; }
    /// <summary>覆盖服务器、实际模型、凭据身份、实现与本地策略的稳定身份；语义变化必须改变。</summary>
    string BindingIdentity { get; }
    /// <summary>提交一次原生生成请求，不重试或后台查询；实现也须先复制调用者数据。</summary>
    Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default);
    /// <summary>显式查询一次既有任务并返回当前原生状态及结果，不自动下载或轮询。</summary>
    Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default);
}
