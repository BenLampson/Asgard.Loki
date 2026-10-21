using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.BedrockStreamFixtures;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>损耗许可不能绕过源消息、工具索引、参数对象、用量和终态完整性。</summary>
public sealed class BedrockStreamIntegrityTests
{
    /// <summary>每个用例都从完整有效源流修改一个原生条件。</summary>
    public static IEnumerable<object[]> Corruptions()
    {
        foreach (var nova in new[] { false, true })
        foreach (var kind in new[] { "missing-start", "duplicate-start", "orphan-delta", "wrong-index", "duplicate-id", "wrong-role",
            "after-stop", "duplicate-stop", "duplicate-metadata", "missing-metrics", "null-metrics", "missing-count", "string-count",
            "string-index", "fraction-index", "negative-index", "delta-union", "closed-delta", "wrong-kind", "open-block", "missing-stop",
            "missing-metadata", "truncated", "delta-id", "null-input", "null-payload" }) yield return [nova, kind];
    }
    /// <summary>不匹配的内容永不先发出成功终态；转换失败后对象不可继续使用。</summary>
    [Theory] [MemberData(nameof(Corruptions))]
    public void InvalidSource_FailsWithoutTerminal(bool nova, string kind)
    {
        var rows = Rows(nova); var bad = rows[0];
        switch (kind)
        {
            case "missing-start": rows.RemoveAt(0); break;
            case "duplicate-start": rows.Insert(1, bad.DeepClone().AsObject()); break;
            case "orphan-delta": rows.RemoveAt(4); break;
            case "wrong-index": Payload(rows[5], nova)["contentBlockIndex"] = 19; break;
            case "duplicate-id": Payload(rows[6], nova)["start"]!["toolUse"]!["toolUseId"] = "stream-a"; break;
            case "wrong-role": Payload(rows[0], nova)["role"] = "user"; break;
            case "after-stop": rows.Insert(14, rows[2].DeepClone().AsObject()); break;
            case "duplicate-stop": rows.Add(rows[13].DeepClone().AsObject()); break;
            case "duplicate-metadata": rows.Add(rows[14].DeepClone().AsObject()); break;
            case "missing-metrics": Payload(rows[14], nova).Remove("metrics"); break;
            case "null-metrics": Payload(rows[14], nova)["metrics"] = null; break;
            case "missing-count": Payload(rows[14], nova)["usage"]!.AsObject().Remove("totalTokens"); break;
            case "string-count": Payload(rows[14], nova)["usage"]!["inputTokens"] = "17"; break;
            case "string-index": Payload(rows[1], nova)["contentBlockIndex"] = "0"; break;
            case "fraction-index": Payload(rows[1], nova)["contentBlockIndex"] = 0.5; break;
            case "negative-index": Payload(rows[1], nova)["contentBlockIndex"] = -1; break;
            case "delta-union": Payload(rows[2], nova)["delta"]!["toolUse"] = new JsonObject { ["input"] = "{}" }; break;
            case "closed-delta": rows.Insert(4, rows[2].DeepClone().AsObject()); break;
            case "wrong-kind": Payload(rows[5], nova)["delta"] = new JsonObject { ["text"] = "wrong" }; break;
            case "open-block": rows.RemoveAt(10); break;
            case "missing-stop": rows.RemoveAt(13); break;
            case "missing-metadata": rows.RemoveAt(14); break;
            case "truncated": rows.RemoveRange(13, 2); break;
            case "delta-id": Payload(rows[5], nova)["delta"]!["toolUse"]!["toolUseId"] = "stream-b"; break;
            case "null-input": Payload(rows[5], nova)["delta"]!["toolUse"]!["input"] = null; break;
            case "null-payload": if (nova) rows[2]["data"]!["contentBlockDelta"] = null; else rows[2]["data"]!["delta"] = null; break;
        }
        var converter = new BedrockProtocolStreamConverter(nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, new() { AllowLoss = true }); List<BedrockStreamConversionResult> seen = [];
        Assert.Throws<InvalidDataException>(() => { foreach (var row in rows) seen.AddRange(Push(converter, row)); seen.AddRange(converter.Complete()); });
        Assert.DoesNotContain(seen, item => item.EventType is "messageStop" or "metadata"); Assert.Throws<InvalidOperationException>(() => converter.Complete());
    }

    /// <summary>完整工具 JSON 既不能被修补，也不能用数组、重复属性或无效转义冒充对象。</summary>
    [Theory] [InlineData(true, "{\"n\":")] [InlineData(false, "{\"n\":")]
    [InlineData(true, "[]")] [InlineData(false, "[]")]
    [InlineData(true, "{\"n\":1,\"n\":2}")] [InlineData(false, "{\"n\":1,\"n\":2}")]
    [InlineData(true, "{\"n\":\"\\uD800\"}")] [InlineData(false, "{\"n\":\"\\uD800\"}")]
    public void InvalidToolObject_IsNeverNormalizedOrCompleted(bool nova, string argument)
    {
        var rows = Rows(nova); Payload(rows[5], nova)["delta"]!["toolUse"]!["input"] = argument;
        Payload(rows[9], nova)["delta"]!["toolUse"]!["input"] = "";
        Assert.Throws<InvalidDataException>(() => Convert(nova, rows, new() { AllowLoss = true }));
    }

    /// <summary>原请求的历史工具 ID 不可复用，即使上游本次流内部没有重复。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void ContextualStream_RejectsHistoricalCallId(bool nova)
    {
        var converter = new BedrockProtocolStreamConverter(Context(nova)); var rows = Rows(nova);
        Payload(rows[4], nova)["start"]!["toolUse"]!["toolUseId"] = "call-a";
        var failure = Assert.Throws<LlmConversionException>(() => { foreach (var row in rows) Push(converter, row); });
        Assert.Equal("tool_call_id_reused", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>推理/签名、服务结果、媒体、引用和托管工具不因损耗许可退化为普通内容。</summary>
    public static IEnumerable<object[]> NativeOnly()
    {
        foreach (var nova in new[] { false, true }) foreach (var field in new[] { "reasoningContent", "toolResult", "image", "video", "citation", "audio", "guardContent", "cachePoint" }) yield return [nova, field];
    }
    /// <summary>不支持内容保留明确硬边界，不把 signed/hosted 状态当作可丢弃元数据。</summary>
    [Theory] [MemberData(nameof(NativeOnly))]
    public void NativeOnlyDelta_RejectsEvenWithLossConsent(bool nova, string field)
    {
        var rows = Rows(nova); Payload(rows[2], nova)["delta"]![field] = new JsonObject();
        var failure = Assert.Throws<LlmConversionException>(() => Convert(nova, rows, new() { AllowLoss = true }));
        Assert.Equal("bedrock_native_only", Assert.Single(failure.Diagnostics).Code);
    }

    /// <summary>原生工具开始的未映射执行种类或完整参数不能丢弃后伪装为普通增量调用。</summary>
    [Theory] [InlineData(true, "type")] [InlineData(false, "type")] [InlineData(true, "input")] [InlineData(false, "input")]
    public void StartToolState_IsNotDowngraded(bool nova, string field)
    {
        var rows = Rows(nova); Payload(rows[4], nova)["start"]!["toolUse"]![field] = "server";
        Assert.Throws<LlmConversionException>(() => Convert(nova, rows, new() { AllowLoss = true }));
    }

    /// <summary>Converse 必须提供源事件名；Nova 不能从外层名称替代缺失的内层终态。</summary>
    [Fact]
    public void NativeEventTypes_AreNotInferredFromBusinessFields()
    {
        var converse = new BedrockProtocolStreamConverter(LlmProtocol.BedrockConverse, LlmProtocol.BedrockNova);
        Assert.Throws<ArgumentNullException>(() => converse.Push(new() { ["type"] = "messageStart", ["role"] = "assistant" }));
        var nova = new BedrockProtocolStreamConverter(LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse);
        var first = Assert.Single(nova.Push(Rows(true)[0]["data"]!.AsObject(), "messageStop")); Assert.Equal("messageStart", first.EventType);
        Assert.Throws<InvalidDataException>(() => nova.Complete());
    }
    /// <summary>Nova 原生已知联合分支冲突是源结构错误，不可通过允许未知字段来绕过。</summary>
    [Fact]
    public void AmbiguousNativeEnvelope_AlwaysRejects()
    {
        var converter = new BedrockProtocolStreamConverter(LlmProtocol.BedrockNova, LlmProtocol.BedrockConverse, new() { AllowLoss = true });
        Assert.Throws<InvalidDataException>(() => converter.Push(new() { ["messageStart"] = new JsonObject { ["role"] = "assistant" }, ["messageStop"] = new JsonObject() }, "chunk"));
    }
}
