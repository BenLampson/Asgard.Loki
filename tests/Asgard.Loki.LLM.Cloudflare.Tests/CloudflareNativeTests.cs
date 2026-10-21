using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Cloudflare.Tests.CloudflareFixture;

namespace Asgard.Loki.LLM.Cloudflare.Tests;

/// <summary>通过公开原生 SDK 比较完整独立请求、原生封装与 SSE 字面帧窗口。</summary>
public sealed class CloudflareNativeTests
{
    /// <summary>URL 拥有实际模型；正文与全部结果、工具参数和原生终态身份都保持原样。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PublicSdk_MatchesIndependentNativeWindows(bool stream)
    {
        var body = new CloudflareBodyStream(Encoding.UTF8.GetBytes(stream ? Text("native.sse") : Part("response").GetRawText()));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(Part("endpoint").GetString(), message.RequestUri!.OriginalString);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-selected-token", message.Headers.Authorization.Parameter);
            var actual = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(Part(stream ? "wire_stream" : "wire_complete"), actual));
            Assert.False(actual.TryGetProperty("model", out _)); Assert.Equal("9007199254740993", actual.GetProperty("future").GetProperty("exact").GetRawText());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); return response;
        });
        using var factory = new CloudflareHttpFactory(handler) { DefaultBearer = "fixture-unselected-default" }; var provider = new CloudflareTextProvider(factory, httpClientName: "explicit-native-client");
        await using (var client = Client(provider))
        {
            var request = Request(); var before = JsonSerializer.Serialize(request);
            if (stream)
            {
                var expected = Part("events").EnumerateArray().ToArray(); List<WorkersAiTextEvent> events = [];
                await foreach (var item in client.LLM.WorkersAiText.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken))
                {
                    if (item.IsTerminal) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
                    events.Add(item);
                }
                Assert.Equal(expected.Length, events.Count);
                for (var i = 0; i < events.Count; i++)
                {
                    Assert.Equal(expected[i].GetProperty("data").GetString(), events[i].Data); Assert.Equal(expected[i].GetProperty("event_name").GetString(), events[i].EventName);
                    Assert.Equal(expected[i].GetProperty("event_id").GetString(), events[i].EventId); Assert.Equal(expected[i].GetProperty("retry").GetInt64(), events[i].RetryMilliseconds);
                    Assert.Equal(i == events.Count - 1, events[i].IsTerminal); Assert.Equal(i != events.Count - 1, events[i].Json.HasValue);
                }
                Assert.Equal("[DONE]", events[^1].Data); Assert.Null(events[^1].Json); Assert.Equal("native-b", events[1].Json!.Value.GetProperty("tool_calls")[1].GetProperty("future_id").GetString());
            }
            else
            {
                var result = await client.LLM.WorkersAiText.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken);
                Assert.Equal(Part("response").GetRawText(), result.Data.GetRawText()); Assert.Null(result.Id);
                Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(Target().UpstreamModel, result.Metadata.UpstreamModel);
            }
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        Assert.Equal(1, handler.Calls); Assert.Equal("explicit-native-client", Assert.Single(factory.Names)); Assert.False(factory.Disposed);
    }
    /// <summary>独立 prompt-only 输入不增加消息/模型/生成默认值；原生空回答不被视为缺失。</summary>
    [Fact]
    public async Task NativePromptOnly_StaysUnwrapped()
    {
        var fixture = Part("prompt_case"); using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.True(JsonElement.DeepEquals(fixture.GetProperty("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token))));
            return new(HttpStatusCode.OK) { Content = new StringContent(Complete, Encoding.UTF8, "application/json") };
        });
        await using var client = Client(new(new CloudflareHttpFactory(handler))); var result = await client.LLM.WorkersAiText.CompleteAsync(JsonSerializer.Deserialize<WorkersAiTextRequest>(fixture.GetProperty("caller"))!, new() { SessionId = "prompt" }, TestContext.Current.CancellationToken);
        Assert.Equal("", result.Data.GetProperty("result").GetProperty("response").GetString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生名字空间斜线保持路径结构，特殊字符只转义一次，不变成查询或另一模型。</summary>
    [Theory] [InlineData("@cf/team/model", "@cf/team/model")]
    [InlineData("@cf/团队/model?draft#x", "@cf/%E5%9B%A2%E9%98%9F/model%3Fdraft%23x")]
    [InlineData("@cf/team/%2F", "@cf/team/%252F")]
    public async Task ModelUrl_UsesExplicitNamespaceSegments(string model, string path)
    {
        using var handler = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("https://proxy.invalid/prefix/accounts/native/ai/run/" + path, message.RequestUri!.OriginalString); Assert.Equal("", message.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Complete, Encoding.UTF8, "application/json") });
        });
        var provider = new CloudflareTextProvider(new CloudflareHttpFactory(handler)); var target = Target() with { BaseAddress = new("https://proxy.invalid/prefix/accounts/native/ai/run/"), UpstreamModel = model };
        await provider.CompleteAsync(Payload(), Context(target), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>数据中的伪完成字段不能完成流；实际控制帧没有 JSON 壳，空生成也不补造文本。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ActualDone_IsIndependentOfBusinessJson(bool empty)
    {
        var record = "{\"response\":\"[DONE]\",\"type\":\"response.completed\",\"done\":true,\"future\":null}"; var body = new CloudflareBodyStream(Encoding.UTF8.GetBytes((empty ? "" : Frame(record)) + Done));
        using var handler = Reply(body, true); var provider = new CloudflareTextProvider(new CloudflareHttpFactory(handler)); List<WorkersAiTextEvent> events = [];
        await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) events.Add(item);
        Assert.Equal(empty ? 1 : 2, events.Count); Assert.True(events[^1].IsTerminal); Assert.Null(events[^1].Json); if (!empty) Assert.False(events[0].IsTerminal);
    }
}
