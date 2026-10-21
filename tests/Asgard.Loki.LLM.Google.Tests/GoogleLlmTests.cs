using Asgard.Loki.LLM.Google;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.Google.Tests;

/// <summary>使用实际 Gemini 适配器执行公共原生协议测试。</summary>
public sealed class GoogleLlmTests : NativeLlmTests
{
    /// <inheritdoc />
    protected override LlmProtocol Protocol => LlmProtocol.Gemini;
    /// <inheritdoc />
    protected override ILlmProvider Provider(IHttpClientFactory factory) => new GoogleLlmProvider(factory);
}
