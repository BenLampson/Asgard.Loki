using System.Text.Json.Nodes;

namespace Asgard.Loki.Audio;

/// <summary>原生音频任务请求，不把音乐/歌词等参数统一成通用提示词模型。</summary>
public sealed record AudioTaskRequest
{
    /// <summary>已配置的公共音频任务路由。</summary>
    public required string Model { get; init; }
    /// <summary>显式供应商操作名，不从公共模型名猜测或改写。</summary>
    public required string Operation { get; init; }
    /// <summary>供应商原始 JSON 字段，公共路由 model 不属于该对象。</summary>
    public required JsonObject Parameters { get; init; }
    /// <summary>在等待或选择目标之前复制原生参数；直接供应商调用也可使用相同快照契约。</summary>
    public AudioTaskRequest Snapshot()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Model); ArgumentException.ThrowIfNullOrWhiteSpace(Operation);
        Internal.AudioTaskJson.Text(Model); Internal.AudioTaskJson.Text(Operation); ArgumentNullException.ThrowIfNull(Parameters);
        if (Parameters.Any(member => member.Key.Equals("model", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("原生字段不能覆盖公共路由 model。");
        return this with { Parameters = Internal.AudioTaskJson.Snapshot(Parameters) };
    }
}
