using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Asgard.Loki.Audio.SunoAPI.Tests;

/// <summary>完整 HTTP、读取和释放边界必须先成功，才能交付任务确认或终态。</summary>
public sealed class SunoLifecycleTests
{
    /// <summary>读取/释放失败不会先交付成功；单次调用不重试、不轮询。</summary>
    [Theory] [InlineData("submit", "read")] [InlineData("get", "read")] [InlineData("batch", "read")] [InlineData("result", "read")]
    [InlineData("submit", "stream-dispose")] [InlineData("get", "stream-dispose")] [InlineData("batch", "stream-dispose")] [InlineData("result", "stream-dispose")]
    [InlineData("submit", "client-dispose")] [InlineData("get", "client-dispose")] [InlineData("batch", "client-dispose")] [InlineData("result", "client-dispose")]
    public async Task ResourceFailure_DoesNotReturnSuccess(string operation, string failure)
    {
        var raw = SunoFixture.Part(operation == "submit" ? "submit" : operation == "result" ? "get_music_result" : "batch_partial");
        if (operation == "submit") raw = raw.GetProperty("music");
        using var stream = new SunoReadStream(Encoding.UTF8.GetBytes(raw.GetRawText())) { FailAfter = failure == "read" ? 3 : null, FailDispose = failure == "stream-dispose" };
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }) };
        if (failure == "client-dispose") factory.OnClientDispose = () => throw new IOException("fixture client dispose failure");
        var adapter = new SunoAudioAdapter(factory, SunoFixture.Options());
        await Assert.ThrowsAsync<IOException>(() => Call(adapter, operation, TestContext.Current.CancellationToken));
        Assert.True(stream.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.False(factory.Disposed);
    }

    /// <summary>调用前取消没有 HTTP；EOF/客户端释放时取消也不能交付成功。</summary>
    [Theory] [InlineData("submit")] [InlineData("get")] [InlineData("batch")] [InlineData("result")]
    public async Task CancellationBeforeAndAtFinalBoundary_IsHonored(string operation)
    {
        using var cancellation = new CancellationTokenSource();
        var raw = operation == "submit" ? SunoFixture.Part("submit").GetProperty("music") : SunoFixture.Part(operation == "result" ? "get_music_result" : "batch_partial");
        using var stream = new SunoReadStream(Encoding.UTF8.GetBytes(raw.GetRawText())) { OnEof = cancellation.Cancel };
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }) };
        var adapter = new SunoAudioAdapter(factory, SunoFixture.Options()); using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(adapter, operation, before.Token)); Assert.Equal(0, factory.Creates);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(adapter, operation, cancellation.Token));
        Assert.Equal(1, factory.Calls); Assert.True(stream.Disposed); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>长度超限、不完整或额外正文、非法 UTF-8 及错误内容类型均不能发布状态。</summary>
    [Theory] [InlineData("limit")] [InlineData("short")] [InlineData("extra")] [InlineData("utf8")] [InlineData("content-type")] [InlineData("charset")]
    public async Task InvalidTransport_FailsWithoutRetry(string kind)
    {
        var bytes = kind == "utf8" ? new byte[] { 0xFF } : Encoding.UTF8.GetBytes(SunoFixture.Part("batch_partial").GetRawText());
        using var stream = new SunoReadStream(bytes); using var factory = new SunoTestFactory { OnSend = (_, _) =>
        {
            var content = new StreamContent(stream);
            if (kind == "short") content.Headers.ContentLength = bytes.Length + 1;
            if (kind == "extra") content.Headers.ContentLength = bytes.Length - 1;
            if (kind == "content-type") content.Headers.ContentType = new("text/html");
            if (kind == "charset") content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-16" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        } };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunoAudioAdapter(factory, SunoFixture.Options(responseLimit: kind == "limit" ? 5 : 100000))
            .GetAsync(SunoFixture.Music, TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>HTTP 失败保留任意原始字节，业务失败保留完整原生 JSON，不把 code 数字化。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeAndHttpErrors_KeepOriginalBodies(bool business)
    {
        var bytes = business ? Encoding.UTF8.GetBytes(SunoFixture.Part("error").GetRawText()) : new byte[] { 0xFF, 0x00, 0x7B };
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(business ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests)
        { Content = new ByteArrayContent(bytes) }) };
        var error = await Assert.ThrowsAsync<SunoAudioException>(() => new SunoAudioAdapter(factory, SunoFixture.Options()).SubmitAsync(SunoFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(bytes, error.Body.ToArray()); Assert.Equal(business ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, error.StatusCode);
        if (business) { Assert.Equal("invalid_request_error", error.NativeCode); Assert.Equal(SunoFixture.Part("error").GetRawText(), error.Response!.Value.GetRawText()); }
        else { Assert.Null(error.NativeCode); Assert.Null(error.Response); }
        Assert.Equal(1, factory.Calls); Assert.DoesNotContain("original native error", error.Message);
    }

    /// <summary>工厂创建客户端时调用者改写参数，也不会改变已准备的原生请求。</summary>
    [Fact]
    public async Task Parameters_AreFrozenBeforeCreatingClient()
    {
        var request = SunoFixture.Request(); var expected = request.Parameters.ToJsonString();
        using var factory = new SunoTestFactory { OnCreate = () => request.Parameters.Clear(), OnSend = async (message, token) =>
        {
            var actual = System.Text.Json.Nodes.JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), actual));
            return SunoFixture.Json(SunoFixture.Part("submit").GetProperty("music"));
        } };
        await new SunoAudioAdapter(factory, SunoFixture.Options()).SubmitAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }

    private static async Task Call(SunoAudioAdapter adapter, string operation, CancellationToken token)
    {
        if (operation == "submit") await adapter.SubmitAsync(SunoFixture.Request(), token);
        else if (operation == "get") await adapter.GetAsync(SunoFixture.Music, token);
        else if (operation == "batch") await adapter.GetBatchAsync([SunoFixture.Music], token);
        else await adapter.GetResultAsync(SunoFixture.Music, token);
    }
}
