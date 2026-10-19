using System.Net;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>同一认证入口下完整 JSON 与两个独立 EventStream 生命周期都保留晚期故障边界。</summary>
public sealed class ApiKeyTransportTests
{
    public static TheoryData<bool, bool, string> LateCases
    {
        get { TheoryData<bool, bool, string> rows = []; foreach (var converse in new[] { false, true }) foreach (var stream in new[] { false, true }) foreach (var fault in new[] { "read", "dispose", "cancel" }) rows.Add(converse, stream, fault); return rows; }
    }
    /// <summary>即使完整响应/终态已经读取，迟读/释放/取消也不能返回成功或公开原生完成尾部。</summary>
    [Theory] [MemberData(nameof(LateCases))]
    public async Task LateFailure_WithholdsSuccessAndReleases(bool converse, bool stream, string failure)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(ApiKeyFixture.Bytes(converse, stream)) { FailAtEof = failure == "read", FailDispose = failure == "dispose", OnEof = failure == "cancel" ? cancel.Cancel : null }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(ApiKeyFixture.Reply(stream, body))); var factory = new AwsHttpFactory(handler); var terminal = false; async Task Invoke() => await ApiKeyFixture.Invoke(new(factory, new() { Region = "us-east-1" }), converse, stream, cancel.Token, item => terminal |= ApiKeyFixture.Terminal(item)); if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Invoke); else await Assert.ThrowsAsync<IOException>(Invoke); Assert.False(terminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>四个方法原始错误状态/正文保持完整，不因 Key 认证失败重试或回退签名。</summary>
    [Theory] [InlineData(403)] [InlineData(429)] [InlineData(503)]
    public async Task HttpErrors_RemainOriginalWithoutRetry(int status)
    {
        const string json = "{\"message\":\"原始认证/服务错误\",\"future\":null}"; foreach (var converse in new[] { false, true }) foreach (var stream in new[] { false, true }) { using var handler = new StubHttpHandler((request, _) => { Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }); }); var factory = new AwsHttpFactory(handler); var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ApiKeyFixture.Invoke(new(factory, new() { Region = "us-east-1" }), converse, stream, TestContext.Current.CancellationToken)); Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(json, error.ResponseBody); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Null(error.GetAwsEventStreamFrame()); }
    }
    /// <summary>HTTP 200 后的模型服务异常仍返回原始错误帧，不释放此前暂存的成功尾部。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EventStreamError_PreservesValidFrameAndDiscardsFinish(bool converse)
    {
        const string json = "{\"message\":\"原始模型错误\",\"future\":false}"; var errorFrame = StreamFixture.Frame("exception", "throttlingException", json); var wire = ApiKeyFixture.Bytes(converse, true).Concat(errorFrame).ToArray(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(wire))); var terminal = false; var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ApiKeyFixture.Invoke(new(new AwsHttpFactory(handler), new() { Region = "us-east-1" }), converse, true, TestContext.Current.CancellationToken, item => terminal |= ApiKeyFixture.Terminal(item))); Assert.False(terminal); Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Equal(json, error.ResponseBody); Assert.Equal(errorFrame, error.GetAwsEventStreamFrame()!.WireBytes.ToArray()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>原生增量后提前退出/取消释放资源，不能为了认证清理而读完生成或补终态。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task EarlyExit_DoesNotReadToCompletion(bool converse, bool cancelAfterFirst)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream(ApiKeyFixture.Bytes(converse, true)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body))); var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new() { Region = "us-east-1" }); var count = 0; async Task Consume() { await foreach (var _ in ApiKeyFixture.Stream(provider, converse, token: cancel.Token)) { count++; if (cancelAfterFirst) cancel.Cancel(); else break; } } if (cancelAfterFirst) await Assert.ThrowsAnyAsync<OperationCanceledException>(Consume); else await Consume(); Assert.Equal(1, count); Assert.False(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
