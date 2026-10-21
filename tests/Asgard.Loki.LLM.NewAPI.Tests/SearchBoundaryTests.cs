using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.NewAPI.Tests.SearchFixture;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>完整原生 HTTP 窗口必须经过预算、真实 EOF、全部关闭与最后取消检查。</summary>
public sealed class SearchBoundaryTests
{
    /// <summary>成功和错误响应都不能忽略晚期读取或自有资源释放失败。</summary>
    [Theory] [InlineData("read", 200)] [InlineData("body", 200)] [InlineData("content", 200)] [InlineData("client", 200)] [InlineData("read", 429)] [InlineData("body", 429)] [InlineData("content", 429)] [InlineData("client", 429)]
    public async Task ResourceFailure_PreventsDelivery(string stage, int status)
    {
        var body = new OllamaBodyStream([1, 2]) { FailAtEof = stage == "read", FailDispose = stage == "body" }; var content = new SearchContent(body) { FailDispose = stage == "content" }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = content })); using var factory = new SearchFactory(handler) { OnDispose = () => { if (stage == "client") throw new IOException("client close"); } }; var client = new NewApiSearchClient(factory, Options(), Credential()); await Assert.ThrowsAsync<IOException>(() => client.SearchAsync(Parameters(), TestContext.Current.CancellationToken)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>末尾读取和每层关闭期间取消都不能返回已经读满的字节窗口。</summary>
    [Theory] [InlineData("eof")] [InlineData("body")] [InlineData("content")] [InlineData("client")]
    public async Task FinalCancellation_PreventsDelivery(string stage)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); var body = new OllamaBodyStream([1, 2]) { OnEof = () => { if (stage == "eof") cancellation.Cancel(); }, OnDispose = () => { if (stage == "body") cancellation.Cancel(); } }; var content = new SearchContent(body) { OnDispose = () => { if (stage == "content") cancellation.Cancel(); } }; using var handler = new StubHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content })); using var factory = new SearchFactory(handler) { OnDispose = () => { if (stage == "client") cancellation.Cancel(); } }; await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NewApiSearchClient(factory, Options(), Credential()).SearchAsync(Parameters(), cancellation.Token)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>调用开始前取消不会创建借用客户端或访问上游。</summary>
    [Fact]
    public async Task CancelledBeforeCall_HasNoIo()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NewApiSearchClient(factory, Options(), Credential()).SearchAsync(Parameters(), cancellation.Token)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>请求预算包含选中模型和完整 UTF-8 编码；精确上限可通过，小一字节失败在 IO 前。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequestBudget_CountsWholeWire(bool exact)
    {
        var parameters = Parameters(); var expected = parameters.DeepClone().AsObject(); expected["model"] = Options().UpstreamModel; var count = System.Text.Encoding.UTF8.GetByteCount(expected.ToJsonString()); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream([])))); using var factory = new SearchFactory(handler); var client = new NewApiSearchClient(factory, Options() with { MaximumRequestBytes = count-(exact ? 0 : 1) }, Credential()); if (exact) await client.SearchAsync(parameters, TestContext.Current.CancellationToken); else await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(parameters, TestContext.Current.CancellationToken)); Assert.Equal(exact ? 1 : 0, handler.Calls); Assert.Equal(exact ? 1 : 0, factory.Clients.Count);
    }
    /// <summary>成功与错误正文都必须在实际字节预算内完整读取，不能交付截断错误。</summary>
    [Theory] [InlineData(200, false)] [InlineData(200, true)] [InlineData(429, false)] [InlineData(429, true)]
    public async Task ResponseBudget_IsComplete(int status, bool exact)
    {
        var body = new OllamaBodyStream([0, 255, 128, 65]); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, status))); using var factory = new SearchFactory(handler); var client = new NewApiSearchClient(factory, Options() with { MaximumResponseBytes = exact ? 4 : 3 }, Credential()); if (!exact) await Assert.ThrowsAsync<InvalidDataException>(() => client.SearchAsync(Parameters(), TestContext.Current.CancellationToken)); else if (status == 429) { var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => client.SearchAsync(Parameters(), TestContext.Current.CancellationToken)); Assert.Equal(new byte[] { 0, 255, 128, 65 }, error.Body.ToArray()); } else { var result = await client.SearchAsync(Parameters(), TestContext.Current.CancellationToken); Assert.Equal(new byte[] { 0, 255, 128, 65 }, result.Body.ToArray()); } Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>无效、超长、欠读和超读的声明长度不被正文完整读取掩盖。</summary>
    [Theory] [InlineData("bad")] [InlineData("negative")] [InlineData("multiple")] [InlineData("over-limit")] [InlineData("under-read")] [InlineData("over-read")]
    public async Task DeclaredLength_MustMatch(string kind)
    {
        var body = new OllamaBodyStream([1, 2]); using var handler = new StubHttpHandler((_, _) => { var response = Reply(body); response.Content.Headers.TryAddWithoutValidation("Content-Length", kind switch { "bad" => "no-length", "negative" => "-1", "multiple" => "2, 2", "over-limit" => "99", "under-read" => "3", _ => "1" }); return Task.FromResult(response); }); using var factory = new SearchFactory(handler); await Assert.ThrowsAsync<InvalidDataException>(() => new NewApiSearchClient(factory, Options() with { MaximumResponseBytes = 8 }, Credential()).SearchAsync(Parameters(), TestContext.Current.CancellationToken)); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
