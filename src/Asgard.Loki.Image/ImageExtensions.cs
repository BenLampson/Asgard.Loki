using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Image;

/// <summary>提供图像模块独立的注册与访问扩展。</summary>
public static class ImageExtensions
{
    extension(ILokiClientBuilder builder)
    {
        /// <summary>注册图像生成模块。</summary>
        public ILokiClientBuilder AddImage(ImageOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var module = new ImageClient(options);
            return builder.AddModule(() => module);
        }
    }
    extension(ILokiClient client)
    {
        /// <summary>取得已注册的图像模块。</summary>
        public ImageClient Image => client.GetModule<ImageClient>();
    }
}
