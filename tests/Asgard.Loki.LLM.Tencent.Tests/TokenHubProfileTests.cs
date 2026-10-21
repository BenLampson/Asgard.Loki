using System.Text.Json;
using Asgard.Loki.LLM.OpenAI;
using Xunit;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>TokenHub 通过显式 OpenAI 目标复用现有 SDK，生产腾讯包不依赖 OpenAI 包或猜测 Key 形状。</summary>
public sealed class TokenHubProfileTests
{
    /// <summary>普通/含竖线的 Key 都是原样 Bearer；完整请求/结果/流事件与独立样本一致。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExplicitCompatibleTargetRetainsNativeWindowWithoutCredentialHeuristics(bool stream, bool pipeKey)
    {
        using var factory = new HunyuanHttpFactory(); var raw = HunyuanFixture.Part("caller", "tokenhub.json"); var request = JsonSerializer.Deserialize<ChatCompletionRequest>(raw)!; var original = JsonSerializer.Serialize(request); var key = pipeKey ? "fixture|opaque|bearer" : "fixture-opaque-bearer";
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HunyuanFixture.Part("endpoint", "tokenhub.json").GetString(), message.RequestUri!.AbsoluteUri); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(key, message.Headers.Authorization.Parameter); Assert.DoesNotContain(message.Headers, h => h.Key.StartsWith("X-TC-", StringComparison.OrdinalIgnoreCase));
            Assert.True(JsonElement.DeepEquals(HunyuanFixture.Part(stream ? "wire_stream" : "wire_complete", "tokenhub.json"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token))));
            return stream ? HunyuanFixture.Sse(HunyuanFixture.NativeSse("tokenhub.sse")) : HunyuanFixture.Reply(HunyuanFixture.Part("response", "tokenhub.json"));
        };
        var provider = new OpenAiLlmProvider(factory, id: "tokenhub-compatible"); var target = HunyuanFixture.Target(HunyuanFixture.Part("root", "tokenhub.json").GetString()!) with { ProviderId = provider.Id, UpstreamModel = "fixture-upstream-chat", CredentialId = "tokenhub-key", Protocols = LlmProtocol.ChatCompletions };
        await using var client = new LokiClientBuilder().AddLLM(new() { Providers = [provider], Credentials = [new("tokenhub-key", key)], SessionStore = HunyuanFixture.Store(), Routes = [new() { Model = "public-chat", Targets = [target] }] }).Build();
        if (!stream)
        {
            var result = await client.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "tokenhub" }, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(HunyuanFixture.Part("response", "tokenhub.json"), result.Data)); Assert.Equal("fixture-upstream-chat", result.Metadata.UpstreamModel);
        }
        else
        {
            var golden = HunyuanFixture.Part("events", "tokenhub.json").EnumerateArray().ToArray(); var index = 0;
            await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request, new() { SessionId = "tokenhub" }, TestContext.Current.CancellationToken))
            {
                Assert.True(JsonElement.DeepEquals(golden[index], item.Data)); Assert.Equal(index < 4 ? "tokenhub-0" : "tokenhub-tail", item.EventId); Assert.Equal(800, item.RetryMilliseconds); index++;
            }
            Assert.Equal(5, index);
        }
        Assert.Equal(original, JsonSerializer.Serialize(request)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
}
