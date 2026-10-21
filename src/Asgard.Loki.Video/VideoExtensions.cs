using Asgard.Loki.Abstractions;
using Asgard.Loki.Video.Internal;

namespace Asgard.Loki.Video;

/// <summary>按现有模块机制注册和访问独立 Video 能力，不修改统一入口依赖。</summary>
public static class VideoExtensions
{
    extension(ILokiClientBuilder builder)
    {
        /// <summary>校验并保存视频配置，模块不拥有借用的适配器或 HTTP 工厂。</summary>
        public ILokiClientBuilder AddVideo(VideoOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder); var configuration = new VideoConfiguration(options);
            return builder.AddModule(() => new VideoClient(configuration));
        }
    }
    extension(ILokiClient client)
    {
        /// <summary>取得已注册的原生视频任务客户端。</summary>
        public VideoClient Video => client.GetModule<VideoClient>();
    }
}
