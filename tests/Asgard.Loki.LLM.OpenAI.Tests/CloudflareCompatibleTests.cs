using Asgard.Loki.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.CloudflareCompatibleFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>通过显式 OpenAI 目标复用 Cloudflare 兼容协议，不引入生产供应商互相引用或模型猜测。</summary>
public sealed class CloudflareCompatibleTests
{
    /// <summary>完整独立 Chat JSON/SSE 和非流式 Responses 保留原生关联、推理、设置与未知字段。</summary>
    [Theory]
    [InlineData("chat", false, false)] [InlineData("chat", false, true)] [InlineData("chat", true, false)] [InlineData("chat", true, true)]
    [InlineData("chat-stream", false, false)] [InlineData("chat-stream", false, true)] [InlineData("chat-stream", true, false)] [InlineData("chat-stream", true, true)]
    [InlineData("responses", false, false)] [InlineData("responses", false, true)] [InlineData("responses", true, false)] [InlineData("responses", true, true)]
    public async Task ExplicitProfile_MatchesWholeNativeWireAndResult(string mode, bool proxy, bool opaqueKey)
    {
        var responses = mode == "responses"; var stream = mode == "chat-stream"; var key = opaqueKey ? "fixture|opaque|token" : "fixture-cloudflare-token";
        var root = Part("root", responses).GetString()!; var endpoint = Part("endpoint", responses).GetString()!;
        if (proxy) { root = root.Replace("https://native.invalid/", "https://proxy.invalid/tenant-prefix/"); endpoint = endpoint.Replace("https://native.invalid/", "https://proxy.invalid/tenant-prefix/"); }
        var reply = stream ? Text("chat.sse") : Part("response", responses).GetRawText(); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(reply));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(endpoint, request.RequestUri!.OriginalString);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal(key, request.Headers.Authorization.Parameter);
            Assert.DoesNotContain(request.Headers, h => h.Key.StartsWith("cf-aig-", StringComparison.OrdinalIgnoreCase));
            var actual = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsByteArrayAsync(token));
            var expected = Part(responses ? "wire" : stream ? "wire_stream" : "wire_complete", responses); Assert.True(JsonElement.DeepEquals(expected, actual));
            Assert.Equal("9007199254740993", actual.GetProperty("future").GetProperty("exact").GetRawText());
            if (!responses) Assert.True(actual.GetProperty("options").GetProperty("rejectIfBusy").GetBoolean());
            else Assert.False(actual.GetProperty("stream").GetBoolean());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); return response;
        });
        var provider = new OpenAiLlmProvider(new StubHttpClientFactory(handler), id: "workers-compatible"); var target = Target(provider, responses, root);
        await using var client = Client(provider, target, key); var call = new LlmCallOptions { SessionId = "workers-profile" };
        if (responses)
        {
            var request = JsonSerializer.Deserialize<ResponseRequest>(Part("caller", true))!; var before = JsonSerializer.Serialize(request);
            var result = await client.LLM.Responses.CreateAsync(request, call, TestContext.Current.CancellationToken);
            Assert.Equal(Part("response", true).GetRawText(), result.Data.GetRawText()); Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.Equal("native-reported-model", result.Data.GetProperty("model").GetString());
            Assert.Equal("opaque-returned", result.Data.GetProperty("output")[0].GetProperty("encrypted_content").GetString()); Assert.Equal("call-returned", result.Data.GetProperty("output")[1].GetProperty("call_id").GetString());
            Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.True(body.Disposed);
        }
        else
        {
            var request = JsonSerializer.Deserialize<ChatCompletionRequest>(Part("caller", false))!; var before = JsonSerializer.Serialize(request);
            if (stream)
            {
                var expected = Part("events", false).EnumerateArray().ToArray(); var index = 0;
                await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request, call, TestContext.Current.CancellationToken))
                {
                    Assert.Equal(expected[index].GetRawText(), item.Data.GetRawText()); Assert.Equal(index < 3 ? "workers-chat-0" : "workers-chat-tail", item.EventId); Assert.Equal(800, item.RetryMilliseconds);
                    Assert.Equal(index == 0 ? "original.native" : null, item.EventName); Assert.Equal(target.UpstreamModel, item.Metadata.UpstreamModel);
                    if (index >= 2) { Assert.True(body.Eof); Assert.True(body.Disposed); } index++;
                }
                Assert.Equal(expected.Length, index);
            }
            else
            {
                var result = await client.LLM.ChatCompletions.CompleteAsync(request, call, TestContext.Current.CancellationToken); Assert.Equal(Part("response", false).GetRawText(), result.Data.GetRawText());
                Assert.Equal("native-reported-model", result.Data.GetProperty("model").GetString()); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.True(body.Disposed);
            }
            Assert.Equal(before, JsonSerializer.Serialize(request));
        }
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>账号路径作为完整服务器身份参与已有 Session，不按同模型或同 Key 静默迁移。</summary>
    [Theory] [InlineData("chat")] [InlineData("responses")]
    public async Task AccountRootDrift_RejectsBoundSessionBeforeAnotherSend(string mode)
    {
        var responses = mode == "responses"; var store = Store(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(Part("response", responses).GetRawText(), Encoding.UTF8, "application/json") }));
        var provider = new OpenAiLlmProvider(new StubHttpClientFactory(handler), id: "workers-compatible"); var target = Target(provider, responses, Part("root", responses).GetString()!);
        await using (var first = Client(provider, target, "fixture", store)) await Invoke(first, responses);
        var changed = target with { BaseAddress = new(target.BaseAddress.OriginalString.Replace(responses ? "responses-account" : "chat-account", "another-account")) };
        await using var second = Client(provider, changed, "fixture", store); await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(second, responses)); Assert.Equal(1, handler.Calls);
    }
    private static async Task Invoke(ILokiClient client, bool responses)
    {
        if (responses) await client.LLM.Responses.CreateAsync(JsonSerializer.Deserialize<ResponseRequest>(Part("caller", true))!, new() { SessionId = "bound" }, TestContext.Current.CancellationToken);
        else await client.LLM.ChatCompletions.CompleteAsync(JsonSerializer.Deserialize<ChatCompletionRequest>(Part("caller", false))!, new() { SessionId = "bound" }, TestContext.Current.CancellationToken);
    }
    private static LlmTarget Target(ILlmProvider provider, bool responses, string root) => new()
    { Id = "workers-account", ProviderId = provider.Id, BaseAddress = new(root), UpstreamModel = Part("model", responses).GetString()!, CredentialId = "workers-key", Protocols = responses ? LlmProtocol.Responses : LlmProtocol.ChatCompletions };
    private static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    private static ILokiClient Client(OpenAiLlmProvider provider, LlmTarget target, string key, ILlmSessionStore? store = null) => new LokiClientBuilder().AddLLM(new()
    { Providers = [provider], Credentials = [new("workers-key", key)], SessionStore = store ?? Store(), Routes = [new() { Model = Part("caller", target.Protocols == LlmProtocol.Responses).GetProperty("model").GetString()!, Targets = [target] }] }).Build();
}
