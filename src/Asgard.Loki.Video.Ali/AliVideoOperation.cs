namespace Asgard.Loki.Video.Ali;

/// <summary>明确选择万相原生视频服务，不能根据模型名称猜测接口。</summary>
public enum AliVideoOperation
{
    /// <summary>services/aigc/video-generation/video-synthesis 原生操作。</summary>
    VideoGeneration,
    /// <summary>services/aigc/image2video/video-synthesis 原生操作，包括匹配该接口的首尾帧或语音驱动模型。</summary>
    ImageToVideo
}
