using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Cohere.Tests;

/// <summary>通过真实公开适配器比较完整独立样本，替代 HTTP 不执行模型或工具。</summary>
public sealed class CohereNativeTests
{
    /// <summary>完整 JSON 和默认 NDJSON 都仅替换实际模型与方法标志，完整历史和工具关联保持原样。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PublicSdk_MatchesWholeIndependentNativeWindows(bool stream)
    {
        var body = new CohereBodyStream(Encoding.UTF8.GetBytes(CohereFixture.Text(stream ? "events.jsonl" : "response.json")));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://native.invalid/proxy/v1/chat", request.RequestUri!.OriginalString);
            Assert.Equal("fixture-selected-key", request.Headers.Authorization!.Parameter); Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.False(request.Headers.Contains("Accepts")); Assert.Equal("fixture-project", Assert.Single(request.Headers.GetValues("X-Client-Name")));
            var expected = JsonNode.Parse(CohereFixture.Text("wire.json"))!.AsObject(); expected["stream"] = stream;
            var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.True(JsonNode.DeepEquals(expected, actual)); Assert.Equal("18446744073709551615", actual["seed"]!.ToJsonString());
            Assert.Equal("9007199254740993", actual["tool_results"]![0]!["call"]!["parameters"]!["precision"]!.ToJsonString());
            Assert.Equal("-0.00", actual["tool_results"]![1]!["outputs"]![0]!["temperature"]!.ToJsonString());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("application/json"); return response;
        });
        using var factory = new CohereHttpFactory(handler) { DefaultBearer = "fixture-unselected-default" };
        var provider = new CohereLlmProvider(factory, httpClientName: "explicit-client", options: new() { ClientName = "fixture-project" });
        await using (var client = CohereFixture.Client(provider))
        {
            var request = CohereFixture.Request(); var original = JsonSerializer.Serialize(request);
            if (stream)
            {
                using var expected = JsonDocument.Parse(CohereFixture.Text("events.json")); List<CohereChatEvent> events = [];
                await foreach (var item in client.LLM.CohereChat.StreamAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken))
                {
                    if (item.IsTerminal) { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
                    Assert.Null(item.EventName); Assert.Null(item.EventId); Assert.Null(item.RetryMilliseconds); events.Add(item);
                }
                Assert.Equal(expected.RootElement.GetArrayLength(), events.Count);
                for (var i = 0; i < events.Count; i++) Assert.Equal(expected.RootElement[i].GetRawText(), events[i].Data.GetRawText());
            }
            else
            {
                var result = await client.LLM.CohereChat.CompleteAsync(request, new() { SessionId = "native" }, TestContext.Current.CancellationToken);
                using var expected = JsonDocument.Parse(CohereFixture.Text("response.json")); Assert.Equal(expected.RootElement.GetRawText(), result.Data.GetRawText());
                Assert.Equal("response/distinct", result.ResponseId); Assert.Equal("generation/原始", result.GenerationId); Assert.Null(result.Id);
                Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
            }
            Assert.Equal(original, JsonSerializer.Serialize(request));
        }
        Assert.False(factory.Disposed); Assert.Equal("explicit-client", Assert.Single(factory.Names)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>同名工具的原生索引和参数分片保持逐条原样，不补 OpenAI ID 或合并 JSON 字符串。</summary>
    [Fact]
    public async Task LegacyOptionalFlagsAndToolFragments_RemainNative()
    {
        var wire = CohereFixture.Text("legacy-tools.jsonl"); var body = new CohereBodyStream(Encoding.UTF8.GetBytes(wire));
        using var handler = Response(body, "application/x-ndjson"); var provider = new CohereLlmProvider(new CohereHttpFactory(handler)); List<CohereChatEvent> events = [];
        await foreach (var item in provider.StreamAsync(CohereFixture.Payload(), CohereFixture.Context(), TestContext.Current.CancellationToken)) events.Add(item);
        var expected = wire.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries); Assert.Equal(expected.Length, events.Count);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], events[i].Data.GetRawText());
        Assert.Equal(new[] { 0, 1, 0 }, events.Skip(1).Take(3).Select(e => e.Data.GetProperty("tool_call_delta").GetProperty("index").GetInt32()));
        Assert.Equal("{\"city\":", events[1].Data.GetProperty("tool_call_delta").GetProperty("parameters").GetString());
        Assert.Equal("separate-tools-response", events[^1].Data.GetProperty("response").GetProperty("response_id").GetString()); Assert.True(events[^1].IsTerminal);
    }
    /// <summary>可选身份与结束原因缺省、空工具规划文本，以及未来/失败结束原因都保留原生含义。</summary>
    [Theory] [InlineData("minimal")] [InlineData("ERROR")] [InlineData("ERROR_TOXIC")] [InlineData("MAX_TOKENS")] [InlineData("FUTURE_REASON")]
    public async Task NativeOptionalAndFinishValues_AreNotNormalized(string kind)
    {
        var json = kind == "minimal" ? CohereFixture.Text("minimal-response.json") : "{\"text\":\"\",\"finish_reason\":\"" + kind + "\"}";
        using var handler = Response(new CohereBodyStream(Encoding.UTF8.GetBytes(json)), "application/json"); var provider = new CohereLlmProvider(new CohereHttpFactory(handler));
        var result = await provider.CompleteAsync(CohereFixture.Payload(), CohereFixture.Context(), TestContext.Current.CancellationToken);
        Assert.True(CohereFixture.Same(json, result.Data)); Assert.Null(result.Id); Assert.Null(result.GenerationId); Assert.Null(result.ResponseId); Assert.Equal(kind == "minimal" ? null : kind, result.FinishReason);
    }
    /// <summary>保留显式根前缀、原始实际模型和空 message，不按模型名称猜测版本或工具协议。</summary>
    [Theory] [InlineData("https://native.invalid/proxy/v1")] [InlineData("http://native.invalid:8081/tenant/custom/")]
    public async Task ExplicitRootAndEmptyMessage_ArePreserved(string root)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(root.TrimEnd('/') + "/chat", request.RequestUri!.OriginalString); var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal("", body["message"]!.GetValue<string>()); Assert.Equal(" raw-model/tag ", body["model"]!.GetValue<string>());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"\"}", Encoding.UTF8, "application/json") };
        });
        var payload = CohereFixture.Payload(); payload["message"] = ""; var provider = new CohereLlmProvider(new CohereHttpFactory(handler));
        await provider.CompleteAsync(payload, CohereFixture.Context(CohereFixture.Target() with { BaseAddress = new(root), UpstreamModel = " raw-model/tag " }), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    internal static StubHttpHandler Response(Stream body, string media = "application/json", int status = 200) => new((_, _) =>
    { var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(media); return Task.FromResult(response); });
}
