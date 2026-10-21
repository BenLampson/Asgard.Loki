using Asgard.Loki.Abstractions;
using Asgard.Loki.Image.Internal;

namespace Asgard.Loki.Image;

/// <summary>提供图像模块独立的注册与访问扩展。</summary>
public static class ImageExtensions
{
    extension(ILokiClientBuilder builder)
    {
        /// <summary>校验并复制图像操作配置，构建的客户端分别持有选择状态。</summary>
        public ILokiClientBuilder AddImage(ImageOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var configuration = new ImageConfiguration(options);
            return builder.AddModule(() => new ImageClient(configuration));
        }
    }
    extension(ILokiClient client)
    {
        /// <summary>取得已注册的图像模块。</summary>
        public ImageClient Image => client.GetModule<ImageClient>();
    }
}
