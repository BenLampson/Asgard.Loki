namespace Asgard.Loki.Video.XAI;

/// <summary>明确选择 xAI 原生生成、编辑或延长操作，不按媒体内容或模型名称切换接口。</summary>
public enum XaiVideoOperation
{
    /// <summary>原生 v1/videos/generations，保留文本、图片、参考和帧输入。</summary>
    Generate = 1,
    /// <summary>原生 v1/videos/edits，保留原始视频输入。</summary>
    Edit = 2,
    /// <summary>原生 v1/videos/extensions，保留原始视频与延长参数。</summary>
    Extend = 3
}
