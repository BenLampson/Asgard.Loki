using System.Text;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>直接构造公共原生请求，避免测试辅助反序列化先替换无效 Unicode 而掩盖快照缺陷。</summary>
public sealed class ToolErrorUnicodeProviderTests
{
    /// <summary>工具错误文本和不同原始 ID 不能在公共快照处都变成替代字符后被成功关联。</summary>
    [Theory] [InlineData(LlmProtocol.ClaudeMessages, false, false)] [InlineData(LlmProtocol.ClaudeMessages, false, true)]
    [InlineData(LlmProtocol.ClaudeMessages, true, false)] [InlineData(LlmProtocol.ClaudeMessages, true, true)]
    [InlineData(LlmProtocol.Gemini, false, false)] [InlineData(LlmProtocol.Gemini, false, true)]
    [InlineData(LlmProtocol.Gemini, true, false)] [InlineData(LlmProtocol.Gemini, true, true)]
    public async Task MalformedUnicode_IsRejectedBeforeHttpAndIdentityCollapse(LlmProtocol source, bool stream, bool identity)
    {
        var target = ToolErrorFixtures.Target(source); using var handler = new StubHttpHandler((_, _) => Task.FromResult(ToolErrorFixtures.Reply(target, stream)));
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: ToolErrorFixtures.Options(LlmToolErrorFormat.GeminiErrorText));
        var raw = ToolErrorFixtures.Read(source, LlmToolErrorFormat.GeminiErrorText); var token = TestContext.Current.CancellationToken;
        if (source == LlmProtocol.ClaudeMessages)
        {
            var messages = raw["messages"]!.AsArray();
            if (identity) { messages[1]!["content"]![1]!["id"] = "\ud800"; messages[2]!["content"]![0]!["tool_use_id"] = "\ud801"; }
            else messages[2]!["content"]![0]!["content"] = "\ud800";
            var request = new ClaudeMessageRequest { Model = "public", MaxTokens = 128, Messages = messages };
            await Assert.ThrowsAsync<EncoderFallbackException>(async () =>
            { if (stream) { await foreach (var _ in client.LLM.ClaudeMessages.StreamAsync(request, new() { SessionId = "bad-unicode" }, token)) { } } else await client.LLM.ClaudeMessages.CreateAsync(request, new() { SessionId = "bad-unicode" }, token); });
        }
        else
        {
            var contents = raw["contents"]!.AsArray();
            if (identity) { contents[1]!["parts"]![1]!["functionCall"]!["id"] = "\ud800"; contents[2]!["parts"]![0]!["functionResponse"]!["id"] = "\ud801"; }
            else contents[2]!["parts"]![0]!["functionResponse"]!["response"]!["error"] = "\ud800";
            var request = new GeminiContentRequest { Model = "public", Contents = contents };
            await Assert.ThrowsAsync<EncoderFallbackException>(async () =>
            { if (stream) { await foreach (var _ in client.LLM.Gemini.StreamAsync(request, new() { SessionId = "bad-unicode" }, token)) { } } else await client.LLM.Gemini.GenerateAsync(request, new() { SessionId = "bad-unicode" }, token); });
        }
        Assert.Equal(0, handler.Calls);
    }
}
