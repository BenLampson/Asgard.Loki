using System.Text;
using Xunit;
using static Asgard.Loki.LLM.Cohere.Tests.CohereFixture;

namespace Asgard.Loki.LLM.Cohere.Tests;

/// <summary>原生工具必填字段不会被空默认值或跨工具重关联掩盖。</summary>
public sealed class CohereToolShapeTests
{
    /// <summary>已知工具/引用事件缺少必填原生载荷时失败，不能只凭后续终态假装协议有效。</summary>
    [Theory]
    [InlineData("{\"event_type\":\"tool-calls-chunk\"}")]
    [InlineData("{\"event_type\":\"tool-calls-chunk\",\"tool_call_delta\":null}")]
    [InlineData("{\"event_type\":\"tool-calls-chunk\",\"tool_call_delta\":{\"parameters\":{}}}")]
    [InlineData("{\"event_type\":\"tool-calls-chunk\",\"tool_call_delta\":{\"index\":\"0\"}}")]
    [InlineData("{\"event_type\":\"tool-calls-generation\"}")]
    [InlineData("{\"event_type\":\"tool-calls-generation\",\"tool_calls\":[{\"name\":\"weather\"}]}")]
    [InlineData("{\"event_type\":\"tool-calls-generation\",\"tool_calls\":[{\"name\":\"weather\",\"parameters\":\"{}\"}]}")]
    [InlineData("{\"event_type\":\"citation-generation\",\"citations\":null}")]
    [InlineData("{\"event_type\":\"search-queries-generation\",\"search_queries\":{}}")]
    public async Task MissingOrMalformedNativePayload_PreventsTerminal(string record)
    {
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes(Start + "\n" + record + "\n" + End)));
        var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; }); Assert.False(terminal);
    }
    /// <summary>完整响应中的工具调用也必须保留原生参数对象，不接受错误的字符串外壳或补造名称。</summary>
    [Theory] [InlineData("{}")] [InlineData("[null]")] [InlineData("[{}]")]
    [InlineData("[{\"name\":null,\"parameters\":{}}]")] [InlineData("[{\"name\":\"weather\",\"parameters\":null}]")]
    [InlineData("[{\"name\":\"weather\",\"parameters\":\"{}\"}]")]
    public async Task InvalidCompleteToolCall_IsRejected(string calls)
    {
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes("{\"text\":\"\",\"tool_calls\":" + calls + "}")));
        await Assert.ThrowsAsync<InvalidDataException>(() => CohereTransportTests.Invoke(new(new CohereHttpFactory(handler)), false));
    }
    /// <summary>可选增量字段缺省或 null 不被填充，索引只保留原始数字而不创建关联表。</summary>
    [Theory] [InlineData("{}")] [InlineData("{\"name\":null,\"parameters\":null,\"text\":null,\"index\":null}")]
    public async Task OptionalToolDeltaFields_RemainAbsentOrNull(string delta)
    {
        var record = "{\"event_type\":\"tool-calls-chunk\",\"tool_call_delta\":" + delta + "}";
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes(Start + "\n" + record + "\n" + End))); var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); List<CohereChatEvent> events = [];
        await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(record, events[1].Data.GetRawText()); Assert.True(events[^1].IsTerminal);
    }
}
