using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Perplexity.Tests.PerplexityFixture;

namespace Asgard.Loki.LLM.Perplexity.Tests;

/// <summary>原生 Responses 根 response_format 不因共用客户端而漏过目标格式校验。</summary>
public sealed class PerplexityOutputFormatTests
{
    /// <summary>完整/流式调用许可时保留根 Schema，拒绝时不创建 HTTP 客户端或调用上游。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task NativeRootFormat_HonorsDeclaration(bool stream, bool allow)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(Golden.GetProperty("caller").GetProperty("response_format"), body.RootElement.GetProperty("response_format")));
            Assert.False(body.RootElement.TryGetProperty("text", out _));
            var bytes = Bytes(stream ? Golden.GetProperty("literal_sse").GetString()! : Golden.GetProperty("complete").GetRawText());
            return Reply(new OllamaBodyStream(bytes), stream, bytes.Length);
        });
        using var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory);
        var target = Target(provider) with
        {
            AllowedOutputFormats = new Dictionary<LlmProtocol, IReadOnlyList<string>> { [LlmProtocol.Responses] = allow ? ["json_schema"] : ["text", "json_object"] }
        };
        await using var sdk = Build(provider, target: target); var request = Golden.GetProperty("caller").Deserialize<ResponseRequest>()!;
        var before = JsonSerializer.Serialize(request);
        async Task Invoke()
        {
            if (stream) { await foreach (var _ in sdk.LLM.Responses.StreamAsync(request, new() { SessionId = "format" }, TestContext.Current.CancellationToken)) { } }
            else await sdk.LLM.Responses.CreateAsync(request, new() { SessionId = "format" }, TestContext.Current.CancellationToken);
        }
        if (allow) await Invoke();
        else
        {
            var error = await Assert.ThrowsAsync<NotSupportedException>(Invoke); Assert.Contains("$.response_format.type", error.Message);
            Assert.Empty(factory.Clients);
        }
        Assert.Equal(allow ? 1 : 0, handler.Calls); Assert.Equal(before, JsonSerializer.Serialize(request));
    }
}
