using System.Text.Json.Nodes;

namespace Asgard.Loki.Video;

/// <summary>原生视频提交参数，供应商内容结构保持于 Parameters，不建立万能提示词模型。</summary>
public sealed record VideoTaskRequest
{
    /// <summary>已配置的公共路由名称，实际模型由完整目标固定。</summary>
    public required string Model { get; init; }
    /// <summary>原生请求字段，不含由路由负责的根级 model；调用入口先复制再等待。</summary>
    public required JsonObject Parameters { get; init; }
    /// <summary>校验并复制完整原生参数，供能力入口与直接调用的供应商共同保持快照契约。</summary>
    public VideoTaskRequest Snapshot()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Model); Internal.VideoJson.Text(Model);
        ArgumentNullException.ThrowIfNull(Parameters);
        if (Parameters.ContainsKey("model")) throw new ArgumentException("原生参数不能覆盖路由选择的实际模型。", nameof(Parameters));
        return this with { Parameters = Internal.VideoJson.Snapshot(Parameters) };
    }
}
