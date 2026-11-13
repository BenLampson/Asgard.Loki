namespace Asgard.Loki.Image;

/// <summary>图片生成或任务的明确状态；未知和接受提交均不表示成功。</summary>
public enum LokiImageGenerationState
{
    /// <summary>上游状态未知。</summary>
    Unknown,
    /// <summary>已提交，等待处理。</summary>
    Pending,
    /// <summary>正在处理。</summary>
    Running,
    /// <summary>已成功生成图片。</summary>
    Succeeded,
    /// <summary>同时包含成功和失败图片。</summary>
    PartiallySucceeded,
    /// <summary>已失败。</summary>
    Failed,
    /// <summary>已取消。</summary>
    Canceled,
    /// <summary>完整响应没有图片，例如安全拦截或合法纯文本结果。</summary>
    NoImage
}
