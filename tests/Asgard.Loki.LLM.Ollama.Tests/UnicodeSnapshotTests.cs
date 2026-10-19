using System.Text.Json;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Ollama.Tests;

/// <summary>Ollama 两个公共原生入口的扩展参数必须在快照和 Session 等待前验证。</summary>
public sealed class UnicodeSnapshotTests
{
    /// <summary>坏参数名和 JSON 字符串转义不能由序列化修复，也不能先创建缓存绑定再由传输碰巧拒绝。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, false, true)] [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)] [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task MalformedExtension_IsRejectedBeforeCache(bool generate, bool stream, bool key)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP"));
        var store = new ControlledSessionStore(OllamaFixture.Store()); var reads = 0; store.BeforeRead = _ => { reads++; return Task.CompletedTask; };
        await using var client = OllamaFixture.Client(new(new StubHttpClientFactory(handler)), store: store, targets: [OllamaFixture.Target() with { Protocols = generate ? LlmProtocol.OllamaGenerate : LlmProtocol.OllamaChat }]);
        var name = key ? "future\ud800" : "future"; var value = JsonSerializer.Deserialize<JsonElement>(key ? "false" : "{\"nested\":[\"\\ud800\"]}");
        var token = TestContext.Current.CancellationToken;
        var error = await Record.ExceptionAsync(async () =>
        {
            if (generate)
            {
                var request = NativeGenerateTests.Request(); request.Parameters[name] = value;
                if (stream) { await foreach (var _ in client.LLM.OllamaGenerate.StreamAsync(request, new() { SessionId = "unicode" }, token)) { } }
                else await client.LLM.OllamaGenerate.GenerateAsync(request, new() { SessionId = "unicode" }, token);
            }
            else
            {
                var request = OllamaFixture.Request(); request.Parameters[name] = value;
                if (stream) { await foreach (var _ in client.LLM.OllamaChat.StreamAsync(request, new() { SessionId = "unicode" }, token)) { } }
                else await client.LLM.OllamaChat.CompleteAsync(request, new() { SessionId = "unicode" }, token);
            }
        });
        Assert.NotNull(error); Assert.Equal(0, reads); Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, handler.Calls);
    }
}
