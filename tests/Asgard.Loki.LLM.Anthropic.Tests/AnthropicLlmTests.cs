using Asgard.Loki.LLM.Anthropic;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>使用实际 Claude 适配器执行公共原生协议测试。</summary>
public sealed class AnthropicLlmTests : NativeLlmTests
{
    /// <inheritdoc />
    protected override LlmProtocol Protocol => LlmProtocol.ClaudeMessages;
    /// <inheritdoc />
    protected override ILlmProvider Provider(IHttpClientFactory factory) => new AnthropicLlmProvider(factory);
}
