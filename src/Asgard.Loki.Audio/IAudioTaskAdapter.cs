namespace Asgard.Loki.Audio;

/// <summary>一个完整音频任务目标，不共用其他能力的协议或状态。</summary>
public interface IAudioTaskAdapter
{
    /// <summary>路由内唯一的目标身份。</summary>
    string Id { get; }
    /// <summary>覆盖服务器、原生模型/操作范围、凭据身份、实现及策略的稳定身份；语义改变必须改变。</summary>
    string BindingIdentity { get; }
    /// <summary>提交一次原生任务，不重试或后台查询。</summary>
    Task<AudioTaskSnapshot> SubmitAsync(AudioTaskRequest request, CancellationToken cancellationToken = default);
    /// <summary>显式查询一次既有任务，不按结果载荷或 HTTP 成功猜测状态。</summary>
    Task<AudioTaskSnapshot> GetAsync(AudioTaskQuery query, CancellationToken cancellationToken = default);
}
