using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.TextCompletionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>文本补全保留既有完整 Session 绑定和非 Responses 的原生 ID 语义。</summary>
public sealed class TextCompletionSessionTests
{
    /// <summary>新增缺省策略省略序列化，精确旧默认绑定字节不因增加新原生接口而改变。</summary>
    [Fact]
    public void DefaultIdentity_PreservesOriginalBytes()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new OpenAiHttpFactory(handler); var provider = new OpenAiLlmProvider(factory);
        var old = typeof(OpenAiLlmProvider).AssemblyQualifiedName + "\nopenai-http-v2\n" + JsonSerializer.Serialize(new { Options = new { MaximumBodyBytes = 32 * 1024 * 1024, MaximumBufferedTerminalEvents = 128 }, Client = "Asgard.Loki.LLM.OpenAI" });
        Assert.Equal(old, provider.BindingIdentity); Assert.Equal(old, new OpenAiLlmProvider(factory, new OpenAiHttpOptions { MaximumTextCompletionRequestBytes = null, MaximumTrackedTextChoices = null }).BindingIdentity); Assert.Empty(factory.Clients);
    }
    /// <summary>所有新增策略和完整目标/凭据/命名客户端/配置版本变化均在第二次发送前拒绝。</summary>
    [Theory] [InlineData("request")] [InlineData("choices")] [InlineData("body")] [InlineData("tail")] [InlineData("root")] [InlineData("model")] [InlineData("key")] [InlineData("client")] [InlineData("version")]
    public async Task ChangedTextBinding_FailsBeforeSecondHttp(string change)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(Part("result").GetRawText())), false))); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store();
        await using (var first = Build(factory, store: store)) await first.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "text-session" }, TestContext.Current.CancellationToken);
        var policy = change switch { "request" => new OpenAiHttpOptions { MaximumTextCompletionRequestBytes = 32 * 1024 * 1024 }, "choices" => new() { MaximumTrackedTextChoices = 2 }, "body" => new() { MaximumBodyBytes = 10000 }, "tail" => new() { MaximumBufferedTerminalEvents = 2 }, _ => new() };
        var target = change switch { "root" => Target() with { BaseAddress = new("https://other.invalid/v1/") }, "model" => Target() with { UpstreamModel = "other" }, "version" => Target() with { Version = "changed" }, _ => Target() };
        await using var second = Build(factory, policy, store, target, change == "key" ? "other-key" : Key, change == "client" ? "other-client" : "Asgard.Loki.LLM.OpenAI");
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "text-session" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>同配置可以重建并继续选择同一完整目标，不把本次原生文本 ID 当 Responses 续接。</summary>
    [Fact]
    public async Task SameBinding_RecoversWithoutResponsesOwnership()
    {
        using var handler = new StubHttpHandler(async (message, token) => { Assert.Equal(Part("endpoint").GetString(), message.RequestUri!.OriginalString); Assert.Equal(Key, message.Headers.Authorization!.Parameter); Assert.True(JsonElement.DeepEquals(Part("wire"), JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)))); return Reply(new OllamaBodyStream(Bytes(Part("result").GetRawText())), false); }); var factory = new OpenAiHttpFactory(handler); var store = CompactionFixture.Store(); var target = Target() with { Protocols = LlmProtocol.TextCompletions | LlmProtocol.Responses };
        await using (var first = Build(factory, store: store, target: target)) await first.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "text-session" }, TestContext.Current.CancellationToken);
        await using var second = Build(factory, store: store, target: target); var result = await second.LLM.TextCompletions.CompleteAsync(Request(), new() { SessionId = "text-session" }, TestContext.Current.CancellationToken); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal("reported-text-model", result.Data.GetProperty("model").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.Responses.CreateAsync(new() { Model = "public-text", PreviousResponseId = "cmpl-text-a" }, new() { SessionId = "text-session" }, TestContext.Current.CancellationToken)); Assert.Equal(2, handler.Calls); Assert.All(factory.Clients, c => Assert.True(c.Disposed));
    }
}
