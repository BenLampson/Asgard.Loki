using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>未知事件完整保留但不得通过同名 response 对象污染原生响应归属。</summary>
public sealed class HttpResponseOwnershipTests
{
    /// <summary>JSON type 与独立 SSE event 两种未知事件均不登记业务 ID；空 ID 也不破坏流。</summary>
    [Theory]
    [InlineData(false, "foreign")] [InlineData(true, "foreign")]
    [InlineData(false, "")] [InlineData(true, "")]
    [InlineData(false, " ")] [InlineData(true, " ")]
    public async Task UnknownResponseObject_IsPreservedWithoutOwnership(bool eventNameOnly, string opaqueId)
    {
        var unknown = eventNameOnly ? JsonSerializer.SerializeToElement(new { response = new { id = opaqueId }, error = new { business = true } })
            : JsonSerializer.SerializeToElement(new { type = "vendor.future", response = new { id = opaqueId }, error = new { business = true } });
        // 未知帧同时出现在原生完成之前和之后；两处都必须完整交付且不登记归属。
        var prefix = "event: vendor.future\n" + Frame(unknown.GetRawText());
        var wire = prefix + Frame(ResponseFinish) + prefix;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(Bytes(wire)), true)));
        var store = CompactionFixture.Store(); await using var client = CompactionFixture.Build(handler, store); List<ResponseEvent> seen = [];
        await foreach (var item in client.LLM.Responses.StreamAsync(new() { Model = "public" }, CompactionFixture.Call(), TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Equal(3, seen.Count); Assert.True(JsonElement.DeepEquals(unknown, seen[0].Data)); Assert.True(JsonElement.DeepEquals(unknown, seen[2].Data));
        var binding = (await store.GetAsync("session", TestContext.Current.CancellationToken))!;
        Assert.True(await store.OwnsResponseAsync(binding, "r", TestContext.Current.CancellationToken));
        if (!string.IsNullOrWhiteSpace(opaqueId))
        {
            Assert.False(await store.OwnsResponseAsync(binding, opaqueId, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.LLM.Responses.CreateAsync(new() { Model = "public", PreviousResponseId = opaqueId }, CompactionFixture.Call(), TestContext.Current.CancellationToken));
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>已知生命周期的归属在事件交付前登记，仍支持 SSE event 名回退。</summary>
    [Theory]
    [InlineData("response.created")] [InlineData("response.in_progress")] [InlineData("response.queued")]
    public async Task KnownLifecycle_RegistersNativeIdBeforeDelivery(string type)
    {
        var wire = "event: " + type + "\n" + Frame("{\"response\":{\"id\":\"r\",\"status\":\"in_progress\"}}") + Frame(ResponseFinish);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new(Bytes(wire)), true))); var store = CompactionFixture.Store();
        await using var client = CompactionFixture.Build(handler, store); var count = 0;
        await foreach (var item in client.LLM.Responses.StreamAsync(new() { Model = "public" }, CompactionFixture.Call(), TestContext.Current.CancellationToken))
        {
            if (count++ == 0) Assert.Equal(type, item.Type);
            var binding = (await store.GetAsync("session", TestContext.Current.CancellationToken))!;
            Assert.True(await store.OwnsResponseAsync(binding, "r", TestContext.Current.CancellationToken));
        }
        Assert.Equal(2, count); Assert.Equal(1, handler.Calls);
    }
}
