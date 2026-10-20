namespace Asgard.Loki.Audio;

/// <summary>公共音频任务路由与完整供应商目标，不能隐式按操作过滤后重调度。</summary>
public sealed record AudioTaskRoute
{
    /// <summary>公开路由别名，不是原生模型目录。</summary>
    public required string Model { get; init; }
    /// <summary>每个目标固定完整服务器、原生模型范围、凭据和适配实现。</summary>
    public required IReadOnlyList<IAudioTaskAdapter> Targets { get; init; }
}
