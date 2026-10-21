using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>入口、完整 JSON 和资源校验不允许数据清理、未绑定认证或提前成功。</summary>
public sealed class ArkBoundaryTests
{
    /// <summary>坏根/原始字符串/重复/别名/认证/预算/预取消在工厂之前失败。</summary>
    [Theory] [InlineData("root")] [InlineData("userinfo")] [InlineData("query")] [InlineData("fragment")] [InlineData("model")] [InlineData("key")] [InlineData("anonymous")] [InlineData("external")] [InlineData("unicode")] [InlineData("element")] [InlineData("duplicate")] [InlineData("alias")] [InlineData("budget")] [InlineData("cancel")]
    public async Task InvalidRequest_FailsBeforeFactory(string fault)
    {
        var payload = Payload(false); var target = Target(); var key = new LlmCredential("ark-key", Key); var policy = new VolcengineHttpOptions();
        switch (fault)
        {
            case "root": target = target with { BaseAddress = new("file:///tmp") }; break;
            case "userinfo": target = target with { BaseAddress = new("https://user:pass@native.invalid/v3/") }; break;
            case "query": target = target with { BaseAddress = new("https://native.invalid/v3/?x=1") }; break;
            case "fragment": target = target with { BaseAddress = new("https://native.invalid/v3/#x") }; break;
            case "model": target = target with { UpstreamModel = new string((char)0xd800, 1) }; break;
            case "key": key = new("ark-key", "invalid key"); break;
            case "anonymous": key = LlmCredential.Anonymous("ark-key"); break;
            case "external": key = LlmCredential.External("ark-key", "principal"); break;
            case "unicode": payload["future"] = new string((char)0xd800, 1); break;
            case "element": payload["future"] = JsonValue.Create(JsonSerializer.Deserialize<JsonElement>("\"\\ud800\"")); break;
            case "duplicate": payload["future"] = JsonNode.Parse("{\"x\":1,\"x\":2}"); break;
            case "alias": payload["Stream"] = true; break;
            case "budget": policy = policy with { MaximumRequestBytes = 1 }; break;
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); using var factory = new ArkHttpFactory(handler); var provider = new VolcengineLlmProvider(factory, policy);
        await Assert.ThrowsAnyAsync<Exception>(() => Complete(provider, false, fault == "cancel" ? new CancellationToken(true) : TestContext.Current.CancellationToken, payload, Context(provider, target, key))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>未知返回字段也须严格 JSON/UTF-8/Unicode，实际目标或媒体改变不能成功。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("unicode")] [InlineData("utf8")] [InlineData("media")] [InlineData("charset")] [InlineData("method")] [InlineData("uri")] [InlineData("redirect")] [InlineData("key")] [InlineData("default-auth")]
    public async Task InvalidResponseOrWire_RejectsWithoutRetry(string fault)
    {
        var bytes = fault == "utf8" ? new byte[] { 0xff } : Bytes(fault == "duplicate" ? "{\"x\":1,\"x\":2}" : fault == "unicode" ? "{\"x\":\"\\ud800\"}" : Part("chat_result").GetRawText()); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((message, _) => { var result = Reply(body, false); if (fault == "media") result.Content.Headers.ContentType = new("text/html"); if (fault == "charset") result.Content.Headers.ContentType!.CharSet = "utf-16"; if (fault == "method") message.Method = HttpMethod.Get; if (fault == "uri") message.RequestUri = new("https://other.invalid/v3/"); if (fault == "redirect") result.RequestMessage = new(HttpMethod.Post, "https://other.invalid/v3/"); if (fault == "key") message.Headers.Authorization = new("Bearer", "other"); return Task.FromResult(result); });
        using var factory = new ArkHttpFactory(handler) { Configure = c => { if (fault == "default-auth") c.DefaultRequestHeaders.Add("x-api-key", "other"); } };
        await Assert.ThrowsAnyAsync<Exception>(() => Complete(new(factory), false, TestContext.Current.CancellationToken)); Assert.Equal(fault == "default-auth" ? 0 : 1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); if (handler.Calls != 0) Assert.True(body.Disposed);
    }
    /// <summary>每个 HTTP 本地资源策略必须为正值，不产生空或无界策略。</summary>
    [Theory] [InlineData("request")] [InlineData("body")] [InlineData("tail")] [InlineData("choices")]
    public void InvalidPolicy_RejectsBeforeFactory(string field)
    {
        var policy = field switch { "request" => new VolcengineHttpOptions { MaximumRequestBytes = 0 }, "body" => new() { MaximumBodyBytes = 0 }, "tail" => new() { MaximumBufferedTerminalEvents = 0 }, _ => new() { MaximumTrackedChoices = 0 } };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); using var factory = new ArkHttpFactory(handler); Assert.Throws<ArgumentOutOfRangeException>(() => new VolcengineLlmProvider(factory, policy)); Assert.Empty(factory.Clients);
    }
}
