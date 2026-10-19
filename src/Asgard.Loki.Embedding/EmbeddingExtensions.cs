using Asgard.Loki.Abstractions;
using Asgard.Loki.Embedding.Internal;

namespace Asgard.Loki.Embedding;

/// <summary>通过现有统一模块机制独立注册并访问嵌入能力。</summary>
public static class EmbeddingExtensions
{
    extension(ILokiClientBuilder builder)
    {
        /// <summary>注册独立嵌入模块并立即保存配置快照。</summary>
        public ILokiClientBuilder AddEmbedding(EmbeddingOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var configuration = new EmbeddingConfiguration(options);
            return builder.AddModule(() => new EmbeddingClient(configuration));
        }
    }
    extension(ILokiClient client)
    {
        /// <summary>取得当前客户端已注册的嵌入模块。</summary>
        public EmbeddingClient Embedding => client.GetModule<EmbeddingClient>();
    }
}
