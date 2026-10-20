using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>从公共原生 SSE 入口验证有歧义关联、缺失/迟到终态及错误尾部不能作为成功交付。</summary>
public sealed class HunyuanStreamStateTests
{
    /// <summary>全部失效场景都不得交付暂存的原生终态，不能补 DONE、丢尾帧或猜索引。</summary>
    [Theory] [InlineData("late-content")] [InlineData("late-reasoning")] [InlineData("late-tools")] [InlineData("duplicate-finish")] [InlineData("changed-id")]
    [InlineData("negative-index")] [InlineData("fraction-index")] [InlineData("string-index")] [InlineData("large-index")] [InlineData("duplicate-index")]
    [InlineData("ambiguous-index")] [InlineData("implicit-conflict")] [InlineData("choices-type")] [InlineData("delta-type")] [InlineData("content-type")] [InlineData("arguments-type")]
    [InlineData("message")] [InlineData("missing-id")] [InlineData("missing-finish")] [InlineData("finish-type")] [InlineData("finish-null")] [InlineData("metadata-only")]
    [InlineData("missing-index-after-multi")] [InlineData("other-unfinished")] [InlineData("truncated")] [InlineData("done")] [InlineData("duplicate-json")]
    public async Task InvalidNativeStateCannotReleaseAnyTerminal(string kind)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var nodes = JsonNode.Parse(HunyuanFixture.Part("events").GetRawText())!.AsArray(); var first = nodes[0]!["Choices"]![0]!; var terminal = nodes[3]!["Choices"]![0]!;
        switch (kind)
        {
            case "late-content": case "late-reasoning": case "late-tools":
                var delta = new JsonObject(); if (kind == "late-tools") delta["ToolCalls"] = new JsonArray(new JsonObject { ["Id"] = "late-call" }); else delta[kind == "late-content" ? "Content" : "ReasoningContent"] = "late";
                nodes.Insert(4, new JsonObject { ["Id"] = "hunyuan-stream", ["Choices"] = new JsonArray(new JsonObject { ["Delta"] = delta, ["FinishReason"] = "" }) }); break;
            case "duplicate-finish": nodes.Insert(4, nodes[3]!.DeepClone()); break;
            case "changed-id": nodes[4]!["Id"] = "different-id"; break;
            case "negative-index": first["Index"] = -1; break;
            case "fraction-index": first["Index"] = 0.5; break;
            case "string-index": first["Index"] = "0"; break;
            case "large-index": first["Index"] = 2147483648L; break;
            case "duplicate-index": first["Index"] = 0; nodes[0]!["Choices"]!.AsArray().Add(first.DeepClone()); break;
            case "ambiguous-index": nodes[0]!["Choices"]!.AsArray().Add(first.DeepClone()); break;
            case "implicit-conflict": nodes[1]!["Choices"]![0]!["Index"] = 2; break;
            case "choices-type": nodes[0]!["Choices"] = false; break;
            case "delta-type": first["Delta"] = false; break;
            case "content-type": first["Delta"]!["Content"] = false; break;
            case "arguments-type": nodes[1]!["Choices"]![0]!["Delta"]!["ToolCalls"]![0]!["Function"]!["Arguments"] = new JsonObject(); break;
            case "message": first["Message"] = new JsonObject(); break;
            case "missing-id": foreach (var node in nodes) node!.AsObject().Remove("Id"); break;
            case "missing-finish": terminal["FinishReason"] = ""; break;
            case "finish-type": terminal["FinishReason"] = false; break;
            case "finish-null": terminal["FinishReason"] = null; break;
            case "metadata-only": foreach (var node in nodes) node!.AsObject().Remove("Choices"); break;
            case "missing-index-after-multi": nodes = JsonNode.Parse(HunyuanFixture.Part("indexed_events").GetRawText())!.AsArray(); nodes[1]!["Choices"]![0]!.AsObject().Remove("Index"); break;
            case "other-unfinished": nodes = JsonNode.Parse(HunyuanFixture.Part("indexed_events").GetRawText())!.AsArray(); nodes.RemoveAt(3); break;
        }
        var text = HunyuanFixture.Frames(nodes.Select(n => JsonSerializer.SerializeToElement(n))); if (kind == "truncated") text = text[..^4]; if (kind == "done") text += "data: [DONE]\r\n\r\n"; if (kind == "duplicate-json") text += "data: {\"Future\":false,\"Future\":true}\r\n\r\n";
        factory.OnSend = (_, _) => Task.FromResult(HunyuanFixture.Sse(text)); List<HunyuanEvent> delivered = []; var provider = new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock());
        Assert.NotNull(await Record.ExceptionAsync(async () => { await foreach (var item in provider.StreamAsync(HunyuanFixture.StreamBody(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)) delivered.Add(item); })); Assert.DoesNotContain(delivered, IsTerminal); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>数字原生 ErrorMsg 的全部原文保留；错误前已暂存的终态永不交付。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeErrorAfterFinishDiscardsTerminalAndKeepsRawError(bool responseBusinessField)
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var error = JsonNode.Parse(HunyuanFixture.Part("stream_error").GetRawText())!.AsObject(); if (responseBusinessField) error["Response"] = new JsonObject { ["Future"] = false }; var data = JsonSerializer.SerializeToElement(error); var text = HunyuanFixture.NativeSse() + HunyuanFixture.Frames([data]); factory.OnSend = (_, _) => Task.FromResult(HunyuanFixture.Sse(text)); List<HunyuanEvent> delivered = [];
        var exception = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in new TencentHunyuanProvider(factory, source, timeProvider: new HunyuanClock()).StreamAsync(HunyuanFixture.StreamBody(), HunyuanFixture.Context(), TestContext.Current.CancellationToken)) delivered.Add(item); });
        Assert.True(JsonElement.DeepEquals(data, JsonSerializer.Deserialize<JsonElement>(exception.Body.Span))); Assert.Equal("text/event-stream; charset=utf-8", exception.ContentType); Assert.DoesNotContain(delivered, IsTerminal); Assert.Equal(3, delivered.Count);
    }
    internal static bool IsTerminal(HunyuanEvent item) => item.Data.TryGetProperty("Choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(c => c.TryGetProperty("FinishReason", out var finish) && finish.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(finish.GetString()));
}
