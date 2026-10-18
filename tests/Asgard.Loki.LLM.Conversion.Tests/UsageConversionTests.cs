using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>按真实协议的计数口径验证缓存和推理用量，而不是仅复制同名字段。</summary>
public sealed class UsageConversionTests
{
    /// <summary>上游未返回计数时保留未知，目标强制要求计数时拒绝补造。</summary>
    [Fact]
    public void MissingUsage_IsNotInvented()
    {
        var response = ConversionFixtures.Response(LlmProtocol.Gemini);
        response.Remove("usageMetadata");
        var chat = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Gemini, LlmProtocol.ChatCompletions);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, chat.Data.GetProperty("usage").ValueKind);
        var error = Assert.Throws<LlmConversionException>(() =>
            LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Gemini, LlmProtocol.ClaudeMessages));
        Assert.Contains(error.Diagnostics, d => d.Code == "missing_usage");
    }

    /// <summary>Claude 开始事件的 input_tokens 必须使用扣除缓存后的计数。</summary>
    [Fact]
    public void ClaudeStreamStart_UsesUncachedInputCount()
    {
        var first = ConversionFixtures.Stream(LlmProtocol.Gemini).First();
        first["usageMetadata"] = JsonNode.Parse("""{"promptTokenCount":10,"cachedContentTokenCount":7,"candidatesTokenCount":0}""");
        var stream = new LlmProtocolStreamConverter(LlmProtocol.Gemini, LlmProtocol.ClaudeMessages);
        var start = stream.Push(first).First().Data.GetProperty("message").GetProperty("usage");
        Assert.Equal(3, start.GetProperty("input_tokens").GetInt64());
        Assert.Equal(7, start.GetProperty("cache_read_input_tokens").GetInt64());
    }

    /// <summary>Claude 非缓存输入与缓存读取必须合并为 OpenAI 的总输入计数。</summary>
    [Fact]
    public void ClaudeCacheRead_IsIncludedInOpenAiPromptTotal()
    {
        var response = ConversionFixtures.Response(LlmProtocol.ClaudeMessages);
        response["usage"] = JsonNode.Parse("""{"input_tokens":3,"output_tokens":2,"cache_read_input_tokens":7,"cache_creation_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":0}}""");
        var result = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.ClaudeMessages, LlmProtocol.ChatCompletions);
        Assert.Empty(result.Diagnostics);
        var usage = result.Data.GetProperty("usage");
        Assert.Equal(10, usage.GetProperty("prompt_tokens").GetInt64()); Assert.Equal(12, usage.GetProperty("total_tokens").GetInt64());
        Assert.Equal(7, usage.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt64());
    }

    /// <summary>反向转换必须从 OpenAI 总输入中扣除缓存读取计数。</summary>
    [Fact]
    public void OpenAiCacheRead_IsExcludedFromClaudeUncachedInput()
    {
        var response = ConversionFixtures.Response(LlmProtocol.ChatCompletions);
        response["usage"] = JsonNode.Parse("""{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12,"prompt_tokens_details":{"cached_tokens":7}}""");
        var result = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages);
        Assert.Equal(3, result.Data.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        Assert.Equal(7, result.Data.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt64());
    }

    /// <summary>Gemini 候选输出不包含推理，OpenAI completion 包含推理。</summary>
    [Fact]
    public void GeminiThoughts_AreIncludedInOpenAiCompletionTotal()
    {
        var response = ConversionFixtures.Response(LlmProtocol.Gemini);
        response["usageMetadata"] = JsonNode.Parse("""{"promptTokenCount":10,"candidatesTokenCount":2,"thoughtsTokenCount":4,"totalTokenCount":16,"cachedContentTokenCount":3}""");
        var result = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Gemini, LlmProtocol.Responses);
        var usage = result.Data.GetProperty("usage");
        Assert.Equal(6, usage.GetProperty("output_tokens").GetInt64()); Assert.Equal(4, usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt64());
        Assert.Equal(3, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt64());
    }

    /// <summary>转换为 Gemini 候选输出时扣除推理 token，保留总量。</summary>
    [Fact]
    public void OpenAiReasoning_IsSeparatedFromGeminiCandidateCount()
    {
        var response = ConversionFixtures.Response(LlmProtocol.Responses);
        response["usage"] = JsonNode.Parse("""{"input_tokens":10,"output_tokens":6,"total_tokens":16,"output_tokens_details":{"reasoning_tokens":4}}""");
        var result = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.Responses, LlmProtocol.Gemini);
        var usage = result.Data.GetProperty("usageMetadata");
        Assert.Equal(2, usage.GetProperty("candidatesTokenCount").GetInt64()); Assert.Equal(4, usage.GetProperty("thoughtsTokenCount").GetInt64());
        Assert.Equal(16, usage.GetProperty("totalTokenCount").GetInt64());
    }

    /// <summary>OpenAI 两种协议之间完整迁移已知明细，不删除显式零值。</summary>
    [Fact]
    public void OpenAiDetails_PreserveExplicitZeroAndPredictionCounts()
    {
        var response = ConversionFixtures.Response(LlmProtocol.ChatCompletions);
        response["usage"]!["completion_tokens_details"] = JsonNode.Parse("""{"reasoning_tokens":0,"accepted_prediction_tokens":1,"rejected_prediction_tokens":0,"audio_tokens":0}""");
        var result = LlmProtocolConverter.ConvertResponse(response, LlmProtocol.ChatCompletions, LlmProtocol.Responses);
        Assert.Equal(1, result.Data.GetProperty("usage").GetProperty("output_tokens_details").GetProperty("accepted_prediction_tokens").GetInt64());
        Assert.True(result.Data.GetProperty("usage").GetProperty("output_tokens_details").TryGetProperty("rejected_prediction_tokens", out _));
    }
}
