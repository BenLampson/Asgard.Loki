using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>公共 SDK 原生完整 JSON/SSE 与独立金样逐字段比较，不构造 Chat 消息或解释模型输出。</summary>
public sealed class AzureNativeTextCompletionTests
{
    /// <summary>三种显式地址与两种认证保留完整请求/结果，并正确区分部署和返回模型。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)]
    public async Task FullNativeJsonMatchesIndependentGolden(int profile, bool external)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureTextFixture.Factory(); using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Part("response").GetRawText()));
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(AzureTextFixture.Part("profiles")[profile].GetProperty("endpoint").GetString(), message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            Assert.True(JsonElement.DeepEquals(AzureTextFixture.Part(profile == 2 ? "dated_complete" : "v1_complete"), AzureCompactionFixture.Element(await message.Content!.ReadAsStringAsync(token))));
            Assert.Equal(external ? "fixture-token-1" : "fixture-key", external ? message.Headers.Authorization!.Parameter : Assert.Single(message.Headers.GetValues("api-key"))); Assert.Equal(external, message.Headers.Authorization is not null); Assert.Equal(!external, message.Headers.Contains("api-key"));
            return new(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        var provider = new AzureLlmProvider(factory, AzureTextFixture.Options(profile), source, timeProvider: clock); await using var client = AzureTextFixture.Build(provider, external, profile); var request = AzureTextFixture.Request(); var before = JsonSerializer.Serialize(request);
        var result = await client.LLM.TextCompletions.CompleteAsync(request, AzureTextFixture.Call(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(AzureTextFixture.Part("response"), result.Data)); Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(new LlmResultMetadata("public", "deploy.text.1", "azure", "primary"), result.Metadata); Assert.Equal("native-returned-model", result.Data.GetProperty("model").GetString());
        Assert.Equal(1, result.Data.GetProperty("choices")[0].GetProperty("index").GetInt32()); Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("future").GetRawText()); Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); Assert.Equal(external ? 1 : 0, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>完整 SSE 保留所有原生选择、logprobs/过滤/未知尾部；首个终态后的事件在 EOF 和释放后交付。</summary>
    [Theory] [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)]
    public async Task FullSseTranscriptRetainsWholeTailAndMetadata(int profile, bool external)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureTextFixture.Factory(); using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureTextFixture.Sse()));
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(AzureTextFixture.Part("profiles")[profile].GetProperty("endpoint").GetString(), message.RequestUri!.AbsoluteUri); Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType);
            Assert.True(JsonElement.DeepEquals(AzureTextFixture.Part(profile == 2 ? "dated_stream" : "v1_stream"), AzureCompactionFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return AzureTextFixture.Streaming(stream);
        };
        var provider = new AzureLlmProvider(factory, AzureTextFixture.Options(profile), source, timeProvider: clock); await using var client = AzureTextFixture.Build(provider, external, profile); var expected = AzureTextFixture.Part("events"); var seen = 0; var request = AzureTextFixture.Request(true); var before = JsonSerializer.Serialize(request);
        string?[] names = ["native.annotation", null, null, null, null, "native.delayed_filter", null, null, "native.future"];
        await foreach (var item in client.LLM.TextCompletions.StreamAsync(request, AzureTextFixture.Call(), TestContext.Current.CancellationToken))
        {
            Assert.True(JsonElement.DeepEquals(expected[seen], item.Data)); Assert.Equal(names[seen], item.EventName); Assert.Equal(seen == 0 ? "filter-0" : seen == 8 ? "future-9" : "delta-1", item.EventId); Assert.Equal(450, item.RetryMilliseconds); Assert.Equal(new LlmResultMetadata("public", "deploy.text.1", "azure", "primary"), item.Metadata);
            if (seen >= 3) { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Disposals); } else Assert.False(stream.Eof); seen++;
        }
        Assert.Equal(expected.GetArrayLength(), seen); Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal(1, factory.Calls); Assert.Equal(external ? 1 : 0, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>SDK 不变更合法原生 prompt 的文本、token 和批次形状，null/省略也不补默认值。</summary>
    [Theory] [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task PromptShapeRetained(int index)
    {
        using var factory = AzureTextFixture.Factory(); var request = AzureTextFixture.Request(); request.Parameters.Remove("prompt"); if (index >= 0) request.Parameters.Add("prompt", AzureTextFixture.Part("prompt_shapes")[index]);
        factory.OnSend = async (message, token) => { var data = AzureCompactionFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.Equal(index >= 0, data.TryGetProperty("prompt", out var prompt)); if (index >= 0) Assert.True(JsonElement.DeepEquals(AzureTextFixture.Part("prompt_shapes")[index], prompt)); return AzureTextFixture.Json(AzureTextFixture.Part("response")); };
        await using var client = AzureTextFixture.Build(new AzureLlmProvider(factory)); await client.LLM.TextCompletions.CompleteAsync(request, AzureTextFixture.Call(), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
}
