using Asgard.Loki.LLM;
using Asgard.Loki.LLM.Volcengine;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>执行火山供应商实际实现的协议兼容性测试。</summary>
public sealed class VolcengineLlmTests : HttpLlmProviderTests
{
    /// <inheritdoc />
    protected override ILlmProvider CreateProvider(IHttpClientFactory factory) => new VolcengineLlmProvider(factory);
}
