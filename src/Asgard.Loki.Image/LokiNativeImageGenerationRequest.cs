using System.Text.Json.Nodes;

namespace Asgard.Loki.Image;

/// <summary>显式固定上游的完整原生图片生成请求，不经过统一参数能力表。</summary>
public sealed record LokiNativeImageGenerationRequest
{
    /// <summary>公开模型名。</summary>
    public required string Model { get; init; }
    /// <summary>必须指定该模型下的完整上游，避免随机选择不同协议。</summary>
    public required string ProviderId { get; init; }
    /// <summary>完整原生正文；model 仍由完整目标配置提供。</summary>
    public required JsonObject Parameters { get; init; }
}
