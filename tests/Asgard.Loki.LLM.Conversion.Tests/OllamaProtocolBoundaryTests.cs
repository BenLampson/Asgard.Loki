using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>新增原生协议不能因为枚举扩展或损耗同意而绕过四协议转换矩阵。</summary>
public sealed class OllamaProtocolBoundaryTests
{
    /// <summary>请求、完整响应、流和同协议复制都显式拒绝未实现的原生协议转换。</summary>
    [Theory] [InlineData(LlmProtocol.OllamaChat, false)] [InlineData(LlmProtocol.OllamaChat, true)]
    [InlineData(LlmProtocol.OllamaGenerate, false)] [InlineData(LlmProtocol.OllamaGenerate, true)]
    [InlineData(LlmProtocol.BedrockNova, false)] [InlineData(LlmProtocol.BedrockNova, true)]
    public void UnsupportedNativeProtocol_IsRejectedInEveryDirection(LlmProtocol native, bool allowLoss)
    {
        var options = new LlmConversionOptions { AllowLoss = allowLoss };
        foreach (var other in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini, native })
        {
            foreach (var pair in new[] { (From: native, To: other), (From: other, To: native) })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertRequest(new JsonObject(), pair.From, pair.To, options));
                Assert.Throws<ArgumentOutOfRangeException>(() => LlmProtocolConverter.ConvertResponse(new JsonObject(), pair.From, pair.To, options));
                Assert.Throws<ArgumentOutOfRangeException>(() => new LlmProtocolStreamConverter(pair.From, pair.To, options));
            }
        }
    }
}
