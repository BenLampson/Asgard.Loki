using Asgard.Loki.LLM;
using Asgard.Loki.LLM.OpenAI;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>执行 OpenAI 供应商实际实现的协议兼容性测试。</summary>
public sealed class OpenAiLlmTests : HttpLlmProviderTests
{
    /// <inheritdoc />
    protected override ILlmProvider CreateProvider(IHttpClientFactory factory) => new OpenAiLlmProvider(factory);
}
