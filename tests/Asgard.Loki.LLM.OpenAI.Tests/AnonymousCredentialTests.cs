using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>API Key 原生供应商的直接适配器入口也不得把显式无认证变成空 Bearer 后发往网络。</summary>
public sealed class AnonymousCredentialTests
{
    /// <summary>基础 HTTP/SSE、原生压缩和 WebSocket 的 Key 访问都在实际请求/握手前硬失败。</summary>
    [Theory] [InlineData("chat")] [InlineData("chat-stream")] [InlineData("responses")] [InlineData("responses-stream")] [InlineData("compaction")] [InlineData("websocket")]
    public async Task DirectNativeOperation_DoesNotDispatchAnonymous(string operation)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected HTTP")); using var sockets = new StubResponsesWebSocketFactory();
        var provider = new OpenAiLlmProvider(new StubHttpClientFactory(handler), new OpenAiResponsesWebSocketOptions(), sockets);
        var target = new LlmTarget { Id = "fixed", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/v1/"), UpstreamModel = "actual", CredentialId = "anonymous",
            Features = LlmTargetFeatures.ResponsesCompaction | LlmTargetFeatures.ResponsesWebSocket };
        var context = new LlmAdapterContext(target, LlmCredential.Anonymous("anonymous"), "public", "session");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var request = new JsonObject { ["model"] = "actual" }; var token = TestContext.Current.CancellationToken;
            switch (operation)
            {
                case "chat": await provider.CompleteAsync(request, context, token); break;
                case "chat-stream": await foreach (var _ in provider.StreamAsync(request, context, token)) { } break;
                case "responses": await provider.CreateAsync(request, context, token); break;
                case "responses-stream": await foreach (var _ in ((IResponsesAdapter)provider).StreamAsync(request, context, token)) { } break;
                case "compaction": await provider.CompactAsync(request, context, token); break;
                case "websocket": await using (var connection = await provider.OpenWebSocketAsync(context, token)) { } break;
            }
        });
        Assert.Equal(0, handler.Calls); Assert.Empty(sockets.Calls);
    }
}
