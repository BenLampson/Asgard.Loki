namespace Asgard.Loki.Audio;

/// <summary>已绑定引用及一次原生音频任务窗口，不启动后台轮询或下载。</summary>
public sealed class AudioTask
{
    internal AudioTask(AudioTaskReference reference, AudioTaskSnapshot snapshot) { Reference = reference; Snapshot = snapshot; }
    /// <summary>可供应用持久化和后续显式查询的绑定引用。</summary>
    public AudioTaskReference Reference { get; }
    /// <summary>本次原生状态和结果。</summary>
    public AudioTaskSnapshot Snapshot { get; }
}
