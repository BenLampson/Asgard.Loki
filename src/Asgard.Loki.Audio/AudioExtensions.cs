using Asgard.Loki.Abstractions;
using Asgard.Loki.Audio.Internal;

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
            ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.SpeechRoutes);
            ArgumentNullException.ThrowIfNull(options.TranscriptionRoutes); ArgumentNullException.ThrowIfNull(options.TranslationRoutes);
            ArgumentNullException.ThrowIfNull(options.TaskRoutes);
            if (options.SpeechRoutes.Count == 0 && options.TranscriptionRoutes.Count == 0 && options.TranslationRoutes.Count == 0 && options.TaskRoutes.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(options), "至少配置一种 Audio 操作的路由。");
            var speech = options.SpeechRoutes.Count > 0 ? new SpeechClient(options) : null;
            var files = new AudioFileConfiguration(options);
            var tasks = new AudioTaskConfiguration(options.TaskRoutes);
            return builder.AddModule(() => new AudioClient(speech,
                files.TranscriptionRoutes.Count > 0 ? new AudioTranscriptionClient(files) : null,
                files.TranslationRoutes.Count > 0 ? new AudioTranslationClient(files) : null,
                tasks.Routes.Count > 0 ? new AudioTaskClient(tasks) : null));
        }
    }

    extension(ILokiClient client)
    {
        /// <summary>取得已注册的音频模块。</summary>
        public AudioClient Audio => client.GetModule<AudioClient>();
    }
}
