using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Internal;

namespace Asgard.Loki.LLM;

/// <summary>通过统一抽象扩展客户端，不引用入口实现。</summary>
public static class LlmExtensions
{
    extension(ILokiClientBuilder builder)
    {
        /// <summary>校验配置并注册 LLM 模块。</summary>
        public ILokiClientBuilder AddLLM(LlmOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var configuration = new LlmConfiguration(options);
            return builder.AddModule(() => new LlmClient(configuration));
        }
    }

    extension(ILokiClient client)
    {
        /// <summary>取得已经注册的 LLM 模块。</summary>
        public LlmClient LLM => client.GetModule<LlmClient>();
    }
}
