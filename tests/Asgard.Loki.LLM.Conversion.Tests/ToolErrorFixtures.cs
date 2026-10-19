using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>两种明确错误格式的四份独立完整原生请求，不由生产转换器生成预期。</summary>
internal static class ToolErrorFixtures
{
    /// <summary>两个方向分别运行两种显式格式。</summary>
    internal static TheoryData<LlmProtocol, LlmToolErrorFormat> Directions()
    {
        TheoryData<LlmProtocol, LlmToolErrorFormat> cases = [];
        foreach (var source in new[] { LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
            foreach (var format in new[] { LlmToolErrorFormat.GeminiResponseObject, LlmToolErrorFormat.GeminiErrorText }) cases.Add(source, format);
        return cases;
    }
    /// <summary>重新解析资源以隔离调用者可变对象。</summary>
    internal static JsonObject Read(LlmProtocol protocol, LlmToolErrorFormat format)
    {
        using var stream = typeof(ToolErrorFixtures).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.Conversion.Tests.Fixtures.tools.tool-errors.json")!;
        return JsonNode.Parse(stream)![format.ToString()]![protocol.ToString()]!.AsObject();
    }
    internal static LlmProtocol Target(LlmProtocol source) => source == LlmProtocol.Gemini ? LlmProtocol.ClaudeMessages : LlmProtocol.Gemini;
    internal static LlmConversionOptions Options(LlmToolErrorFormat format) => new() { ToolErrorFormat = format, DefaultMaxTokens = 128 };
    /// <summary>第一份结果属于逆序的 call_b，第二份属于 call_a。</summary>
    internal static JsonObject Result(JsonObject request, LlmProtocol protocol, int index = 0)
        => (protocol == LlmProtocol.Gemini ? request["contents"]![2]!["parts"]![index]!["functionResponse"] : request["messages"]![2]!["content"]![index])!.AsObject();
    /// <summary>原生文本回复仅验证转换流水线，不假称工具在测试中被执行。</summary>
    internal static HttpResponseMessage Reply(LlmProtocol protocol, bool stream)
    {
        if (!stream) return new(HttpStatusCode.OK) { Content = new StringContent(HostedSearchFixtures.TextResponse(protocol).ToJsonString(), Encoding.UTF8, "application/json") };
        var text = string.Concat(HostedSearchFixtures.TextStream(protocol).Select(item => "data: " + item.ToJsonString() + "\n\n"));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(text)) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
    }
}
