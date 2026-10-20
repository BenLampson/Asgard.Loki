using System.Text.Json.Nodes;

namespace Asgard.Loki.Video;

/// <summary>原生视频提交参数，供应商内容结构保持于 Parameters，不建立万能提示词模型。</summary>
public sealed record VideoTaskRequest
{
    /// <summary>已配置的公共路由名称，实际模型由完整目标固定。</summary>
    public required string Model { get; init; }
    /// <summary>原生请求字段，不含由路由负责的根级 model；调用入口先复制再等待。</summary>
    public required JsonObject Parameters { get; init; }
}
