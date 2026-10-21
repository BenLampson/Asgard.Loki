namespace Asgard.Loki.Video.Vidu;

/// <summary>显式选择 Vidu 原生生成操作，不依赖图片数量或模型名称推断。</summary>
public enum ViduVideoOperation
{
    /// <summary>原生 ent/v2/text2video 文生视频。</summary>
    TextToVideo = 1,
    /// <summary>原生 ent/v2/img2video 图生视频。</summary>
    ImageToVideo = 2,
    /// <summary>原生 ent/v2/start-end2video 首尾帧生视频。</summary>
    StartEndToVideo = 3,
    /// <summary>原生 ent/v2/reference2video 参考生视频。</summary>
    ReferenceToVideo = 4
}
