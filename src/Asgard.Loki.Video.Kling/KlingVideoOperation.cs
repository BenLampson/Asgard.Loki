namespace Asgard.Loki.Video.Kling;

/// <summary>原生生成服务及对应查询路径，不根据模型名称或图片参数切换。</summary>
public enum KlingVideoOperation
{
    /// <summary>原生 v1/videos/text2video 提交与同一服务的任务查询。</summary>
    TextToVideo = 1,
    /// <summary>原生 v1/videos/image2video 提交与同一服务的任务查询。</summary>
    ImageToVideo = 2
}
