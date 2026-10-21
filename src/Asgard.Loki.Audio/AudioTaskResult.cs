using System.Text.Json;

namespace Asgard.Loki.Audio;

/// <summary>原生结果视图及请求关联；结果载荷本身不保证任务已完成。</summary>
public sealed class AudioTaskResult
{
    internal AudioTaskResult(AudioTaskReference reference, JsonElement data) { Reference = reference; Data = data.Clone(); }
    /// <summary>已验证的完整目标及任务引用。</summary>
    public AudioTaskReference Reference { get; }
    /// <summary>完整原生结果 JSON，不推断生命周期、下载 URL 或生成 HTML。</summary>
    public JsonElement Data { get; }
}
