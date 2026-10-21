using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.BedrockStreamFixtures;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>原生逻辑事件双向快照、原文、调用关联与正常结束后的终态发布。</summary>
public sealed class BedrockStreamConversionTests
{
    /// <summary>完整事件名/载荷/信封匹配独立金样，终态在 Complete 之前均不交付。</summary>
    [Theory] [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public void CompleteNativeStreams_MatchGoldensIncrementally(bool nova, bool contextual)
    {
        var context = contextual ? Context(nova) : null;
        var converter = contextual ? new BedrockProtocolStreamConverter(context!) : new BedrockProtocolStreamConverter(
            nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova);
        var rows = Rows(nova); var before = JsonSerializer.Serialize(rows); List<BedrockStreamConversionResult> actual = [];
        for (var i = 0; i < rows.Count; i++)
        {
            var delta = Push(converter, rows[i]); if (i >= 13) Assert.Empty(delta); else Assert.Single(delta);
            actual.AddRange(delta);
        }
        Assert.Equal(13, actual.Count); actual.AddRange(converter.Complete()); EqualGolden(nova, actual);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(rows[i]["data"]), actual[i].OriginalData));
            Assert.Equal(rows[i]["eventName"]!.GetValue<string>(), actual[i].SourceEventName); Assert.Same(context, actual[i].Context); Assert.Empty(actual[i].Diagnostics);
        }
        Assert.Equal(before, JsonSerializer.Serialize(rows)); Assert.Throws<InvalidOperationException>(() => converter.Complete());
        Assert.Throws<InvalidOperationException>(() => Push(converter, rows[0]));
    }

    /// <summary>error/type 业务扩展并非协议错误；默认严格失败，显式许可保留路径和原文。</summary>
    public static IEnumerable<object[]> Extensions()
    {
        foreach (var nova in new[] { false, true }) foreach (var json in new[] { "null", "false", "0", "[]", "{\"type\":\"error\",\"n\":9007199254740993}" }) yield return [nova, json];
    }
    /// <summary>已知事件的非等价扩展只按字段诊断，不扫描工具参数的同名字段。</summary>
    [Theory] [MemberData(nameof(Extensions))]
    public void KnownExtensions_RespectLossPolicyAndOriginalJson(bool nova, string json)
    {
        var rows = Rows(nova); var data = Payload(rows[2], nova); data["error"] = JsonNode.Parse(json); data["type"] = "messageStop";
        var failure = Assert.Throws<LlmConversionException>(() => Convert(nova, rows));
        Assert.DoesNotContain(failure.Diagnostics, item => item.Code == "upstream_stream_error"); Assert.Contains(failure.Diagnostics, item => item.Path == (nova ? "$event.contentBlockDelta.error" : "$event.error"));
        var actual = Convert(nova, rows, new() { AllowLoss = true }); EqualGolden(nova, actual);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(rows[2]["data"]), actual[2].OriginalData));
        Assert.Contains(actual[^1].Diagnostics, item => item.Path == (nova ? "$event.contentBlockDelta.type" : "$event.type")); Assert.Contains(actual[^1].Diagnostics, item => item.Path == (nova ? "$event.contentBlockDelta.error" : "$event.error"));
    }

    /// <summary>迟到的未知事件按损耗许可处理，不能覆盖真正终态或让已暂存成功提前泄漏。</summary>
    [Theory] [InlineData(true, true)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public void UnknownTail_RetainsDiagnosticsWithoutInventingEvent(bool nova, bool loss)
    {
        var rows = Rows(nova); var unknown = JsonNode.Parse("""{"eventName":"vendor.future","data":{"type":"messageStop","error":{"type":"error"},"nil":null}}""")!.AsObject();
        rows.Insert(14, unknown);
        if (!loss) { var failure = Assert.Throws<LlmConversionException>(() => Convert(nova, rows)); Assert.Contains(failure.Diagnostics, item => item.Code == "unknown_event"); }
        else
        {
            var actual = Convert(nova, rows, new() { AllowLoss = true }); EqualGolden(nova, actual);
            Assert.Contains(actual[^1].Diagnostics, item => item.Code == "unknown_event"); Assert.DoesNotContain(actual[^1].Diagnostics, item => item.Code == "upstream_stream_error");
        }
    }

    /// <summary>明确原生服务异常无论损耗许可都失败，不回显原始错误文本。</summary>
    [Theory] [InlineData(true, "modelStreamErrorException")] [InlineData(false, "modelStreamErrorException")]
    [InlineData(true, "throttlingException")] [InlineData(false, "throttlingException")]
    [InlineData(true, "internalServerException")] [InlineData(false, "internalServerException")]
    public void NativeErrors_CloseConverterAndDiscardHeldSuccess(bool nova, string name)
    {
        var converter = new BedrockProtocolStreamConverter(nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, new() { AllowLoss = true });
        var rows = Rows(nova); foreach (var row in rows) Push(converter, row);
        var data = nova ? new JsonObject { [name] = new JsonObject { ["message"] = "private-error" } } : new JsonObject { ["message"] = "private-error" };
        var failure = Assert.Throws<LlmConversionException>(() => converter.Push(data, nova ? "chunk" : name));
        Assert.Equal("upstream_stream_error", Assert.Single(failure.Diagnostics).Code); Assert.DoesNotContain("private-error", failure.Message);
        Assert.Throws<InvalidOperationException>(() => converter.Complete()); Assert.Throws<InvalidOperationException>(() => Push(converter, rows[0]));
    }

    /// <summary>暂存尾帧也必须在 Push 时快照，Complete 不能读取调用者已改写的原文。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void InputMutation_DoesNotChangeIncrementalOrTerminalSnapshots(bool nova)
    {
        var converter = new BedrockProtocolStreamConverter(nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova);
        var rows = Rows(nova); List<BedrockStreamConversionResult> actual = [];
        foreach (var row in rows) { actual.AddRange(Push(converter, row)); row["data"] = new JsonObject { ["mutated"] = true }; }
        actual.AddRange(converter.Complete()); EqualGolden(nova, actual);
        Assert.All(actual, item => Assert.DoesNotContain("mutated", item.OriginalData.GetRawText()));
    }

    /// <summary>原生源复制不以字段名误解终态或加转换过滤，也不对同协议索引做归一化。</summary>
    [Theory] [InlineData(LlmProtocol.BedrockNova)] [InlineData(LlmProtocol.BedrockConverse)]
    public void NativeIdentity_CopyLeavesOpaqueAndUnknownDataUntouched(LlmProtocol protocol)
    {
        var converter = new BedrockProtocolStreamConverter(protocol, protocol, new() { MaxBedrockStreamBlocks = 1, MaxBedrockStreamDiagnostics = 1 });
        var source = JsonNode.Parse("""{"messageStart":{},"messageStop":{},"type":"error","signature":"opaque","future":null}""")!.AsObject();
        var actual = Assert.Single(converter.Push(source, "vendor.raw"));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source), actual.Data)); Assert.Empty(actual.Diagnostics);
        Assert.Equal("vendor.raw", actual.SourceEventName); Assert.Empty(converter.Complete());
    }

    /// <summary>Nova 信封和成员内的同名扩展有不同源路径，不能混成同一条损耗记录。</summary>
    [Fact]
    public void NativeEnvelopeAndPayloadLosses_HaveDistinctPaths()
    {
        var rows = Rows(true); rows[0]["data"]!["type"] = "error"; Payload(rows[0], true)["type"] = "messageStop";
        var actual = Convert(true, rows, new() { AllowLoss = true }); EqualGolden(true, actual);
        Assert.Equal(new[] { "$event.type", "$event.messageStart.type" }, actual[^1].Diagnostics.Select(item => item.Path));
    }
}
