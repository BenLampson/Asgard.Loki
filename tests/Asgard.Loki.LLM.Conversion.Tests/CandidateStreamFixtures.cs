using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>完整多候选流预期独立于生产转换器，每个测试重新读取已提交样本。</summary>
internal static class CandidateStreamFixtures
{
    /// <summary>显式开启新模式，不修改旧单候选默认配置。</summary>
    internal static LlmConversionOptions Options(bool allowLoss = false) => new()
    { EnableMultipleCandidateStreams = true, AllowLoss = allowLoss };
    /// <summary>每次返回独立的 JSON 样本。</summary>
    internal static JsonObject Case(string name) => GeminiCandidateFixtures.Read("chat-gemini-streams", name);
    /// <summary>创建本次原生方向的转换器，不共享流状态。</summary>
    internal static LlmProtocolStreamConverter Converter(JsonObject sample, LlmConversionOptions? options = null)
        => new(Enum.Parse<LlmProtocol>(sample["from"]!.GetValue<string>()), Enum.Parse<LlmProtocol>(sample["to"]!.GetValue<string>()), options ?? Options());
    /// <summary>枚举原始事件顺序，不复制生产侧终态或工具拼接逻辑。</summary>
    internal static List<JsonObject> Source(JsonObject sample) => sample["source"]!.AsArray().Select(n => n!.AsObject()).ToList();
    /// <summary>只检查协议声明的候选结束字段，不扫描业务 JSON 的同名键。</summary>
    internal static bool IsTerminal(LlmConversionResult item)
    {
        var root = item.Data;
        if (root.TryGetProperty("choices", out var choices))
            return choices.EnumerateArray().Any(c => c.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != System.Text.Json.JsonValueKind.Null);
        return root.GetProperty("candidates").EnumerateArray().Any(c => c.TryGetProperty("finishReason", out _));
    }
}
