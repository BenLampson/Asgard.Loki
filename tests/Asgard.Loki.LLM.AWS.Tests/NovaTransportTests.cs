using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Nova 完整 JSON 路径的实际 HTTP/资源/最终取消回归，不用其他协议结果替代验证。</summary>
public sealed class NovaTransportTests
{
    /// <summary>两种认证的原生成功字节之后仍可能读取、释放失败或取消。</summary>
    public static IEnumerable<object[]> Faults()
    {
        foreach (var apiKey in new[] { false, true }) foreach (var fault in new[] { "read", "body", "client", "eof-cancel", "body-cancel", "client-cancel" }) yield return [apiKey, fault];
    }
    /// <summary>晚期故障必须阻止完整结果作为成功返回，原生调用只发出一次。</summary>
    [Theory] [MemberData(nameof(Faults))]
    public async Task LateFaults_WithholdCompleteResult(bool apiKey, string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var clock = new AwsClock(); var source = new AwsCredentialSource(clock);
        var body = new OllamaBodyStream(MantleFixture.Bytes(NovaFixture.Golden().GetProperty("response").GetRawText())) { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = fault == "eof-cancel" ? cancel.Cancel : null, OnDispose = fault == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(body, false))); var factory = new AwsHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("client"); if (fault == "client-cancel") cancel.Cancel(); } };
        var provider = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" }) : new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        var error = await Record.ExceptionAsync(() => provider.InvokeNovaAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: apiKey), cancel.Token));
        if (fault.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(apiKey ? 0 : 1, source.Calls);
    }
    /// <summary>仅异步释放发生的异常和取消也必须先于 Nova 成功返回。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AsyncOnlyDisposal_IsObserved(bool cancelled)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new MantleAsyncDisposeStream(MantleFixture.Bytes("{\"output\":{}}"), () => { if (cancelled) cancel.Cancel(); }, !cancelled);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(body, false))); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" });
        var error = await Record.ExceptionAsync(() => provider.InvokeNovaAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: true), cancel.Token));
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error); Assert.True(body.AsyncDisposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原始正文长度及限额精确检查，不能截断后作为 JSON 成功交付。</summary>
    [Theory] [InlineData("exact")] [InlineData("limit")] [InlineData("short")] [InlineData("long")]
    public async Task RawBodyBounds_AreExact(string scenario)
    {
        var bytes = MantleFixture.Bytes(NovaFixture.Golden().GetProperty("response").GetRawText()); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(body, false, bytes.Length + (scenario == "short" ? -1 : scenario == "long" ? 1 : 0)))); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1", MaximumBodyBytes = bytes.Length - (scenario == "limit" ? 1 : 0) });
        if (scenario == "exact") await provider.InvokeNovaAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: true), TestContext.Current.CancellationToken);
        else await Assert.ThrowsAsync<InvalidDataException>(() => provider.InvokeNovaAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: true), TestContext.Current.CancellationToken));
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>普通和签名模式均不能容忍非法完整表示或错误媒体类型。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task InvalidRepresentation_IsRejected(bool apiKey)
    {
        var clock = new AwsClock(); foreach (var fault in new[] { "array", "duplicate", "escape", "utf8", "media" })
        {
            var text = fault switch { "array" => "[]", "duplicate" => "{\"future\":{\"x\":1,\"x\":2}}", "escape" => "{\"future\":\"\\uD800\"}", _ => "{}" }; var bytes = fault == "utf8" ? new byte[] { 0xFF } : MantleFixture.Bytes(text);
            using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(new MemoryStream(bytes), fault == "media"))); var factory = new AwsHttpFactory(handler); var provider = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" }) : new AwsLlmProvider(factory, new AwsCredentialSource(clock), new() { Region = "us-east-1" }, timeProvider: clock);
            var error = await Record.ExceptionAsync(() => provider.InvokeNovaAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: apiKey), TestContext.Current.CancellationToken)); Assert.True(error is InvalidDataException or JsonException or DecoderFallbackException, error?.ToString()); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
        }
    }
    /// <summary>平台原生错误保留状态和正文，不能被 Nova 的正常 stopReason 或输出覆盖。</summary>
    [Theory] [InlineData(200)] [InlineData(429)] [InlineData(503)]
    public async Task NativeErrors_PreserveOriginalResponse(int status)
    {
        const string error = "{\"error\":{\"code\":\"native\",\"future\":false},\"output\":{\"message\":{\"content\":[{\"text\":\"not success\"}]}},\"stopReason\":\"end_turn\"}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(error)), false, status: (HttpStatusCode)status))); var provider = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" });
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(() => provider.InvokeNovaAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: true), TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.Equal(1, handler.Calls);
    }
}
