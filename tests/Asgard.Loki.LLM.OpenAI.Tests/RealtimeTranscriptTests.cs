using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>独立样本先固定原生工具关联、错误和非成功终态，不依赖生产实现生成预期值。</summary>
public sealed class RealtimeTranscriptTests
{
    /// <summary>确认双向完整窗口保留关联、未知值及原生状态，并明确这不是供应商实测输出。</summary>
    [Fact]
    public void IndependentTranscript_ContainsNativeBoundaries()
    {
        using var source = typeof(RealtimeTranscriptTests).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.OpenAI.Tests.Fixtures.realtime.json")!;
        using var document = JsonDocument.Parse(source);
        var root = document.RootElement;
        Assert.Contains("not live", root.GetProperty("provenance").GetProperty("kind").GetString());
        Assert.Equal("wss://a.invalid/custom/v1/realtime?model=actual-a", root.GetProperty("address").GetString());
        var server = root.GetProperty("server").EnumerateArray().ToArray();
        Assert.Equal(["completed", "cancelled", "incomplete", "failed"], server.Where(e => e.GetProperty("type").GetString() == "response.done")
            .Select(e => e.GetProperty("response").GetProperty("status").GetString()));
        var tool = server.Single(e => e.GetProperty("type").GetString() == "response.function_call_arguments.done");
        var output = root.GetProperty("client").EnumerateArray().Single(e => e.TryGetProperty("item", out var item)
            && item.GetProperty("type").GetString() == "function_call_output").GetProperty("item");
        Assert.Equal(tool.GetProperty("call_id").GetString(), output.GetProperty("call_id").GetString());
        Assert.Equal("client_bad", server.Single(e => e.GetProperty("type").GetString() == "error").GetProperty("error").GetProperty("event_id").GetString());
        Assert.Equal("𐐀你好", server[^1].GetProperty("payload").GetProperty("unicode").GetString());
    }
}
