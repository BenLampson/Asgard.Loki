using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立编写文本数组及图像/PDF 工具结果的完整原生请求和金样。</summary>
internal static class RichToolResultFixtures
{
    /// <summary>具备文本数组结果的三个原生协议。</summary>
    internal static LlmProtocol[] TextProtocols => [LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages];

    /// <summary>六个文本方向和两个媒体方向分别验证，不能暗示 Gemini 存在相同数组结构。</summary>
    internal static TheoryData<LlmProtocol, LlmProtocol, bool> Directions()
    {
        TheoryData<LlmProtocol, LlmProtocol, bool> data = [];
        foreach (var from in TextProtocols) foreach (var to in TextProtocols) if (from != to) data.Add(from, to, false);
        data.Add(LlmProtocol.Responses, LlmProtocol.ClaudeMessages, true); data.Add(LlmProtocol.ClaudeMessages, LlmProtocol.Responses, true);
        return data;
    }

    /// <summary>每次返回独立 JSON，不通过生产转换器生成预期结构。</summary>
    internal static JsonObject Read(LlmProtocol protocol, bool media = false, bool golden = false)
    {
        using var stream = typeof(RichToolResultFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.tools.rich-results.json") ?? throw new InvalidDataException("富工具结果样本缺失。");
        return JsonNode.Parse(stream)![golden ? "golden" : "source"]![media ? "media" : "text"]![protocol.ToString()]!.AsObject();
    }

    /// <summary>定位完整请求中的结果项，保留不同协议的原生容器。</summary>
    internal static JsonObject Result(JsonObject request, LlmProtocol protocol) => (protocol switch
    {
        LlmProtocol.Responses => request["input"]![2],
        LlmProtocol.ClaudeMessages => request["messages"]![2]!["content"]![0],
        _ => request["messages"]![2]
    })!.AsObject();

    /// <summary>结果数组不是普通用户消息，也不是待解析的业务 JSON 字符串。</summary>
    internal static JsonArray Parts(JsonObject request, LlmProtocol protocol) => Result(request, protocol)[protocol == LlmProtocol.Responses ? "output" : "content"]!.AsArray();

    /// <summary>替换结果内容种类，用于验证空数组、字符串和非法值的区别。</summary>
    internal static void SetContent(JsonObject request, LlmProtocol protocol, JsonNode? value)
        => Result(request, protocol)[protocol == LlmProtocol.Responses ? "output" : "content"] = value;
}
