using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立原生样本及手工约定的完整 JSON/事件快照覆盖十二个转换方向。</summary>
public sealed class ProtocolSnapshotTests
{
    /// <summary>覆盖所有异协议方向和三种报文；快照不由生产转换器生成。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, string> Matrix
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol, string> cases = [];
            foreach (var from in Protocols)
            foreach (var to in Protocols)
            foreach (var mode in new[] { "request", "response", "stream" })
                if (from != to) cases.Add(from, to, mode);
            return cases;
        }
    }

    /// <summary>验证全部字段和事件顺序，并额外检查原始身份及诊断，避免只接受漂亮快照。</summary>
    [Theory]
    [MemberData(nameof(Matrix))]
    public void NativeFixtures_MatchIndependentGolden(LlmProtocol from, LlmProtocol to, string mode)
    {
        var original = Load("source", from, mode); var before = original.ToJsonString();
        JsonNode actual;
        if (mode == "stream")
        {
            var state = new LlmProtocolStreamConverter(from, to);
            var events = new JsonArray();
            foreach (var source in original.AsArray())
                foreach (var output in state.Push(source!.AsObject())) events.Add(JsonNode.Parse(output.Data.GetRawText()));
            foreach (var output in state.Complete()) events.Add(JsonNode.Parse(output.Data.GetRawText()));
            Assert.Empty(state.Diagnostics); actual = events;
            Assert.Contains("call_1", actual.ToJsonString());
            Assert.Contains("lookup", actual.ToJsonString());
        }
        else
        {
            var result = mode == "request" ? LlmProtocolConverter.ConvertRequest(original.AsObject(), from, to)
                : LlmProtocolConverter.ConvertResponse(original.AsObject(), from, to);
            Assert.Empty(result.Diagnostics);
            Assert.True(JsonNode.DeepEquals(original, JsonNode.Parse(result.OriginalData.GetRawText())));
            actual = JsonNode.Parse(result.Data.GetRawText())!;
            Assert.Contains("call_1", actual.ToJsonString());
        }
        var expected = Load("golden", to, mode);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{from} → {to} {mode}\nExpected: {expected}\nActual: {actual}");
        Assert.Equal(before, original.ToJsonString());
    }

    /// <summary>从嵌入资源读取固定样本，不依赖工作目录、网络或运行时更新快照。</summary>
    private static JsonNode Load(string folder, LlmProtocol protocol, string mode)
    {
        var name = protocol switch { LlmProtocol.ChatCompletions => "chat", LlmProtocol.Responses => "responses", LlmProtocol.ClaudeMessages => "claude", _ => "gemini" };
        using var stream = typeof(ProtocolSnapshotTests).Assembly.GetManifestResourceStream(
            $"Asgard.Loki.LLM.Conversion.Tests.Fixtures.{folder}.{name}.{mode}.json") ?? throw new InvalidDataException("协议快照资源缺失。");
        return JsonNode.Parse(stream)!;
    }

    /// <summary>明确的四种原生协议。</summary>
    private static readonly LlmProtocol[] Protocols = [LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];
}
