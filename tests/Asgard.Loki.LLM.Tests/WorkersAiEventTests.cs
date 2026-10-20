using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>原生 JSON 和 DONE 控制数据分别保真，不以业务 type 或事件名伪造结束。</summary>
public sealed class WorkersAiEventTests
{
    private static readonly LlmResultMetadata Metadata = new("public", "actual", "provider", "target");
    /// <summary>只有实际完整控制字面具有终态意义；原生空响应和未知字段仍是 JSON。</summary>
    [Theory] [InlineData("[DONE]", true)] [InlineData("{\"response\":\"[DONE]\"}", false)]
    [InlineData("{\"type\":\"response.completed\",\"done\":true}", false)] [InlineData("{\"response\":\"\",\"future\":null}", false)]
    public void NativeDataAndTransportMetadata_AreIndependent(string data, bool done)
    {
        var item = new WorkersAiTextEvent(data, Metadata, "done", "original-id", 0);
        Assert.Equal(data, item.Data); Assert.Equal(done, item.IsTerminal); Assert.Equal(!done, item.Json.HasValue);
        Assert.Equal("done", item.EventName); Assert.Equal("original-id", item.EventId); Assert.Equal(0, item.RetryMilliseconds); Assert.Same(Metadata, item.Metadata);
        if (item.Json is { } json) Assert.Equal(data, json.GetRawText());
    }
    /// <summary>非对象、重复属性、无效转义和控制帧相似文本不能被当成合法原生帧。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("0")] [InlineData(" [DONE]")] [InlineData("[DONE] ")]
    [InlineData("{\"x\":1,\"x\":2}")] [InlineData("{\"future\":\"\\ud800\"}")]
    public void MalformedData_IsNotNormalized(string data) => Assert.NotNull(Record.Exception(() => new WorkersAiTextEvent(data, Metadata)));
    /// <summary>原始 UTF-16 和传输身份中的孤立代理字符在任何序列化修复前拒绝。</summary>
    [Theory] [InlineData("data")] [InlineData("name")] [InlineData("id")]
    public void InvalidOriginalUnicode_IsRejected(string kind)
    {
        var invalid = new string((char)0xd800, 1);
        Assert.Throws<System.Text.EncoderFallbackException>(() => new WorkersAiTextEvent(kind == "data" ? invalid : "{}", Metadata, kind == "name" ? invalid : null, kind == "id" ? invalid : null));
    }
    /// <summary>空引用明确失败，不补造空对象或没有目标的控制帧。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void NullDataOrMetadata_IsRejected(bool metadata)
        => Assert.Throws<ArgumentNullException>(() => new WorkersAiTextEvent(metadata ? "{}" : null!, metadata ? null! : Metadata));
}
