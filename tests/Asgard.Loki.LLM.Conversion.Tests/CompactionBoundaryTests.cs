using System.Text.Json.Nodes;
using Asgard.Loki.LLM.OpenAI;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>压缩状态属于原生协议，允许普通字段损耗也不能丢弃加密窗口或冒充扩展操作。</summary>
public sealed class CompactionBoundaryTests
{
    /// <summary>请求、响应和流中的不透明压缩项都必须明确拒绝跨协议转换。</summary>
    [Theory]
    [InlineData("request", false)] [InlineData("request", true)]
    [InlineData("response", false)] [InlineData("response", true)]
    [InlineData("stream", false)] [InlineData("stream", true)]
    public void OpaqueCompaction_IsNeverDiscarded(string mode, bool allowLoss)
    {
        var item = JsonNode.Parse("""{"type":"compaction","id":"cp_1","encrypted_content":"opaque"}""")!;
        var options = new LlmConversionOptions { AllowLoss = allowLoss };
        var failure = Assert.Throws<LlmConversionException>(() =>
        {
            if (mode == "request")
            {
                var request = ConversionFixtures.Request(LlmProtocol.Responses); request["input"]!.AsArray().Insert(0, item);
                LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, LlmProtocol.ChatCompletions, options);
            }
            else if (mode == "response")
            {
                var response = ConversionFixtures.Response(LlmProtocol.Responses); response["output"]!.AsArray().Insert(0, item);
                LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Responses, LlmProtocol.ChatCompletions, options);
            }
            else
            {
                var state = new LlmProtocolStreamConverter(LlmProtocol.Responses, LlmProtocol.ChatCompletions, options);
                state.Push(ConversionFixtures.Stream(LlmProtocol.Responses).First());
                state.Push(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item });
            }
        });
        Assert.Contains(failure.Diagnostics, d => d.Code == "opaque_state");
    }

    /// <summary>同协议复制仍然完整保留加密项，不受跨协议拒绝规则影响。</summary>
    [Fact]
    public void NativeIdentity_PreservesCompactionItems()
    {
        var request = JsonNode.Parse("""{"model":"m","input":[{"type":"compaction","id":"cp1","encrypted_content":"opaque","future":false}]}""")!.AsObject();
        var result = LlmProtocolConverter.ConvertRequest(request, LlmProtocol.Responses, LlmProtocol.Responses);
        Assert.True(JsonNode.DeepEquals(request, JsonNode.Parse(result.Data.GetRawText()))); Assert.Empty(result.Diagnostics);
    }

    /// <summary>转换装饰器不因借用 OpenAI 适配器就宣称支持原生压缩。</summary>
    [Fact]
    public void Decorator_DoesNotAdvertiseNativeCompaction()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("No HTTP expected"));
        ILlmProvider provider = new ConvertingLlmProvider(new OpenAiLlmProvider(new StubHttpClientFactory(handler)), LlmProtocol.Responses, "wrapper");
        Assert.False(provider is IResponsesCompactionAdapter);
    }
}
