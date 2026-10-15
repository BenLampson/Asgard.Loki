using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Audio;

/// <summary>独立提供音频模块的注册与访问扩展。</summary>
public static class AudioExtensions
{
    extension(ILokiClientBuilder builder)
    {
        /// <summary>校验音频配置并注册能力模块。</summary>
        public ILokiClientBuilder AddAudio(AudioOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var speech = new SpeechClient(options);
            return builder.AddModule(() => new AudioClient(speech));
        }
    }

    extension(ILokiClient client)
    {
        /// <summary>取得已注册的音频模块。</summary>
        public AudioClient Audio => client.GetModule<AudioClient>();
    }
}
