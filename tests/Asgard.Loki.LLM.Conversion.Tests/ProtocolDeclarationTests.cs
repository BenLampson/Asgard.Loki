using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>转换组合的明确协议声明必须与实现矩阵一致，并尊重固定上游自身的支持声明。</summary>
public sealed class ProtocolDeclarationTests
{
    /// <summary>新增文本物理接口不能让另一个装饰器绕过单一固定上游规则。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.Responses)]
    [InlineData(LlmProtocol.ClaudeMessages)] [InlineData(LlmProtocol.Gemini)]
    public void TextInterfaces_DoNotPermitNestedBedrockDecoration(LlmProtocol protocol)
    {
        var native = new DeclaredProtocolProvider(LlmProtocol.BedrockConverse);
        var bedrock = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "native-converted");
        Assert.True(bedrock.SupportsProtocol(protocol));
        Assert.Throws<ArgumentException>(() => new ConvertingLlmProvider(bedrock, protocol, "nested"));
        Assert.Equal(0, native.Calls);
    }

    /// <summary>声明的是当前实现的公开入口，None/组合位/其他原生协议不会因枚举新增被开放。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void DecoratorProtocolDeclarations_MatchCurrentImplementedMatrix(bool bedrock)
    {
        var native = new DeclaredProtocolProvider(bedrock ? LlmProtocol.BedrockConverse : LlmProtocol.ChatCompletions);
        ILlmProtocolSupport provider = bedrock ? new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit")
            : new ConvertingLlmProvider(native, LlmProtocol.ChatCompletions, "explicit");
        foreach (var protocol in Enum.GetValues<LlmProtocol>())
        {
            var expected = bedrock ? protocol is LlmProtocol.BedrockConverse or LlmProtocol.BedrockNova or LlmProtocol.ChatCompletions or LlmProtocol.Responses or LlmProtocol.ClaudeMessages or LlmProtocol.Gemini
                : protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses or LlmProtocol.ClaudeMessages or LlmProtocol.Gemini;
            Assert.Equal(expected, provider.SupportsProtocol(protocol));
        }
        Assert.False(provider.SupportsProtocol(LlmProtocol.BedrockConverse | LlmProtocol.BedrockNova));
        Assert.False(provider.SupportsProtocol(LlmProtocol.ChatCompletions | LlmProtocol.Responses)); Assert.Equal(0, native.Calls);
    }

    /// <summary>固定上游即使提供物理接口，也不能绕过它明确拒绝的原生协议。</summary>
    [Theory] [InlineData(LlmProtocol.ChatCompletions)] [InlineData(LlmProtocol.BedrockConverse)] [InlineData(LlmProtocol.BedrockNova)]
    public void UndeclaredFixedUpstream_IsRejectedBeforeAnyCall(LlmProtocol protocol)
    {
        var native = new DeclaredProtocolProvider(LlmProtocol.None);
        Assert.Throws<ArgumentException>(() =>
        {
            if (protocol == LlmProtocol.ChatCompletions) _ = new ConvertingLlmProvider(native, protocol, "explicit");
            else _ = new ConvertingBedrockLlmProvider(native, protocol, "explicit");
        });
        Assert.Equal(0, native.Calls);
    }
}
