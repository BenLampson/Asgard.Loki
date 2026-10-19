using Asgard.Loki.Abstractions;
using Asgard.Loki.Rerank.Internal;

namespace Asgard.Loki.Rerank;

/// <summary>通过现有统一模块机制独立注册并访问重排能力。</summary>
public static class RerankExtensions
{
    extension(ILokiClientBuilder builder)
    {
        /// <summary>注册独立重排模块并立即保存配置快照。</summary>
        public ILokiClientBuilder AddRerank(RerankOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var configuration = new RerankConfiguration(options);
            return builder.AddModule(() => new RerankClient(configuration));
        }
    }
    extension(ILokiClient client)
    {
        /// <summary>取得当前客户端已注册的重排模块。</summary>
        public RerankClient Rerank => client.GetModule<RerankClient>();
    }
}
