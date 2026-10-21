using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Perplexity.Tests.PerplexityFixture;

namespace Asgard.Loki.LLM.Perplexity.Tests;

/// <summary>原始参数、完整目标和认证前置边界；失败不能通过参数修补或调用另一目标掩盖。</summary>
public sealed class PerplexityBoundaryTests
{
    /// <summary>原始 Unicode、嵌入元素、大小写别名及字节预算在工厂创建前失败。</summary>
    [Theory] [InlineData("unicode")] [InlineData("property")] [InlineData("element")] [InlineData("container")] [InlineData("duplicates")] [InlineData("MODEL")] [InlineData("Stream")] [InlineData("input")] [InlineData("budget")] [InlineData("cancel")]
    public async Task OriginalRequestFault_PrecedesFactory(string fault)
    {
        var payload = Payload(); var invalid = new string((char)0xd800, 1);
        switch (fault) { case "unicode": payload["future"] = invalid; break; case "property": payload[invalid] = false; break; case "element": payload["future"] = JsonValue.Create(JsonSerializer.Deserialize<JsonElement>("\"\\ud800\"")); break; case "container": payload["future"] = JsonValue.Create(new Dictionary<string, string> { ["x"] = invalid }); break; case "duplicates": payload["future"] = JsonNode.Parse("{\"x\":1,\"x\":2}"); break; case "MODEL": case "Stream": payload[fault] = false; break; case "input": payload["input"] = false; break; }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory, fault == "budget" ? new() { MaximumRequestBytes = 1 } : null); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); if (fault == "cancel") cancel.Cancel();
        var failure = await Record.ExceptionAsync(() => provider.CreateAsync(payload, Context(provider), cancel.Token)); Assert.NotNull(failure); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>坏根、模型或认证都不能借用默认身份，也不能创建客户端。</summary>
    [Theory] [InlineData("relative")] [InlineData("query")] [InlineData("fragment")] [InlineData("userinfo")] [InlineData("model")] [InlineData("key")] [InlineData("anonymous")] [InlineData("external")]
    public async Task InvalidTarget_PrecedesFactory(string fault)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new PerplexityHttpFactory(handler); var provider = new PerplexityResponsesProvider(factory); var target = Target(provider);
        target = fault switch { "relative" => target with { BaseAddress = new("relative", UriKind.Relative) }, "query" => target with { BaseAddress = new("https://native.invalid/v1/?x=1") }, "fragment" => target with { BaseAddress = new("https://native.invalid/v1/#x") }, "userinfo" => target with { BaseAddress = new("https://a:b@native.invalid/v1/") }, "model" => target with { UpstreamModel = new string((char)0xd800, 1) }, _ => target };
        var credential = fault switch { "anonymous" => LlmCredential.Anonymous("key"), "external" => LlmCredential.External("key", "principal"), "key" => new("key", "bad key"), _ => new("key", "selected-key") };
        Assert.NotNull(await Record.ExceptionAsync(() => provider.CreateAsync(Payload(), Context(provider, target: target, credential: credential), TestContext.Current.CancellationToken))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>显式资源上限必须为正，不通过溢出或隐式无限制绕过策略。</summary>
    [Theory] [InlineData("request")] [InlineData("response")] [InlineData("stream")] [InlineData("tail")]
    public void InvalidPolicy_IsRejected(string field)
    {
        var options = field switch { "request" => new PerplexityResponsesOptions { MaximumRequestBytes = 0 }, "response" => new() { MaximumResponseBytes = 0 }, "stream" => new() { MaximumStreamBytes = 0 }, _ => new() { MaximumBufferedTerminalEvents = 0 } };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new PerplexityHttpFactory(handler); Assert.Throws<ArgumentOutOfRangeException>(() => new PerplexityResponsesProvider(factory, options)); Assert.Empty(factory.Clients);
    }
    /// <summary>其他 Key 或签名默认头不能与选中 Bearer 混发，工厂自身不被修改或释放。</summary>
    [Theory] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")] [InlineData("x-amz-date")] [InlineData("x-amz-security-token")] [InlineData("x-amz-content-sha256")]
    public async Task ForeignAuthentication_IsRejected(string header)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new PerplexityHttpFactory(handler) { Configure = client => client.DefaultRequestHeaders.TryAddWithoutValidation(header, "foreign") }; var provider = new PerplexityResponsesProvider(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(Payload(), Context(provider), TestContext.Current.CancellationToken)); Assert.True(Assert.Single(factory.Clients).DefaultRequestHeaders.Contains(header)); Assert.True(factory.Clients[0].Disposed); Assert.False(factory.Disposed); Assert.Equal(0, handler.Calls);
    }
}
