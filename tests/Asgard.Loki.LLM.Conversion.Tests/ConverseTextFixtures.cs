using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立完整请求/结果金样；仅规范 SDK 合成 ID，不修改工具 ID、参数或原始数据。</summary>
internal static class ConverseTextFixtures
{
    /// <summary>两个验收程序集共享纯测试资源，不依赖生产写出器或供应商网络。</summary>
    internal static JsonObject Read(LlmProtocol protocol, string key)
    {
        using var stream = typeof(ConverseTextFixtures).Assembly.GetManifestResourceStream("ConverseTextConversion.Fixture")!;
        var root = JsonNode.Parse(stream)!; return (key == "nativeResponse" ? root[key] : root[protocol.ToString()]![key])!.AsObject();
    }
    /// <summary>完整比较包括嵌套空值、精确整数、Schema 和结果容器顺序。</summary>
    internal static void Equal(JsonObject expected, JsonElement actual) => Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), actual), actual.GetRawText());
    /// <summary>原生 Converse 无响应 ID，只有 SDK 明确生成的根/消息标签允许金样归一化。</summary>
    internal static JsonObject NormalizeResponse(JsonElement actual, LlmProtocol protocol)
    {
        var data = JsonNode.Parse(actual.GetRawText())!.AsObject(); var key = protocol == LlmProtocol.Gemini ? "responseId" : "id";
        var id = data[key]!.GetValue<string>(); Assert.StartsWith("loki_", id); Assert.Matches("^loki_[0-9a-f]{32}$", id); data[key] = "__ID__";
        if (protocol == LlmProtocol.Responses) foreach (var node in data["output"]!.AsArray())
            if (node!["type"]!.GetValue<string>() == "message") node["id"] = node["id"]!.GetValue<string>().Replace(id, "__ID__", StringComparison.Ordinal);
        return data;
    }
}
