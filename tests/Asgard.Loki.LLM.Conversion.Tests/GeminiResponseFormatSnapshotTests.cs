using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立完整 Schema 请求快照覆盖现代 Gemini 外壳的六个方向，不自动更新预期值。</summary>
public sealed class GeminiResponseFormatSnapshotTests
{
    /// <summary>每个异协议与 Gemini 的正反请求都由手工样本约束完整报文。</summary>
    [Theory]
    [InlineData(LlmProtocol.ChatCompletions, false)] [InlineData(LlmProtocol.ChatCompletions, true)]
    [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public void CompleteRequest_MatchesAuthoredSnapshot(LlmProtocol other, bool toGemini)
    {
        using var stream = typeof(GeminiResponseFormatSnapshotTests).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.schema.response-format.json") ?? throw new InvalidDataException("结构化请求快照缺失。");
        var fixture = JsonNode.Parse(stream)!.AsObject(); var from = toGemini ? other : LlmProtocol.Gemini; var to = toGemini ? LlmProtocol.Gemini : other;
        var source = fixture[from.ToString()]!.AsObject(); var expected = fixture[to.ToString()]!; var before = source.ToJsonString();
        var result = LlmProtocolConverter.ConvertRequest(source, from, to, new()
        { GeminiOutputFormat = GeminiOutputFormat.ResponseFormat, DefaultSchemaName = "result", AllowLoss = toGemini });
        var actual = JsonNode.Parse(result.Data.GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{from} → {to}\nExpected: {expected}\nActual: {actual}");
        if (!toGemini || other == LlmProtocol.ClaudeMessages) Assert.Empty(result.Diagnostics);
        else Assert.Contains(result.Diagnostics, item => item.Code == "format_metadata_not_representable");
        Assert.Equal(before, source.ToJsonString());
    }
}
