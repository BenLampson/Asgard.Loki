using System.Net;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Audio.Azure.Tests;

/// <summary>原生终态延迟到传输所有权收尾，输入及工厂始终借用。</summary>
public sealed class AzureLifecycleTests
{
    /// <summary>读取、长度、上限、释放或取消故障均不交付语音成功帧。</summary>
    [Theory]
    [InlineData("read")] [InlineData("length")] [InlineData("limit")]
    [InlineData("empty")] [InlineData("dispose")] [InlineData("client-dispose")]
    [InlineData("cancel")] [InlineData("cancel-client")]
    public async Task SpeechCompletionIsDelayed(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var source = new AzureReadStream(kind == "empty" ? [] : AzureFixture.Audio) { FailAfter = kind == "read" ? 3 : null,
            FailDispose = kind == "dispose", OnEof = () => { if (kind == "cancel") cancellation.Cancel(); } };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
            response.Content.Headers.ContentType = new("audio/pcm"); response.Content.Headers.ContentLength = kind == "length" ? 9 : kind == "empty" ? 0 : 8;
            return Task.FromResult(response);
        });
        var factory = new AzureTestFactory(handler) { OnClientDispose = () =>
            { if (kind == "client-dispose") throw new IOException("client dispose"); if (kind == "cancel-client") cancellation.Cancel(); } };
        var seen = new List<SpeechChunk>();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var chunk in new AzureSpeechAdapter(factory, AzureFixture.Options(audio: kind == "limit" ? 4 : 65536))
                .StreamNativeAsync(AzureFixture.Speech(), cancellation.Token)) seen.Add(chunk);
        });
        Assert.All(seen, chunk => Assert.False(chunk.IsCompleted)); Assert.True(source.Disposed); Assert.Equal(1, factory.ClientDisposals); Assert.False(factory.Disposed);
    }
    /// <summary>文件响应 EOF/释放或客户端最后取消都不返回已完成结果。</summary>
    [Theory] [InlineData("read")] [InlineData("length")] [InlineData("dispose")] [InlineData("cancel")] [InlineData("client-dispose")]
    public async Task FileCompletionIsDelayed(string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var upload = new AzureReadStream(AzureFixture.Upload);
        var bytes = AzureFixture.Result(false); var source = new AzureReadStream(bytes) { FailAfter = kind == "read" ? 3 : null,
            FailDispose = kind == "dispose", OnEof = () => { if (kind == "cancel") cancellation.Cancel(); } };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
            response.Content.Headers.ContentType = new("application/json"); response.Content.Headers.ContentLength = bytes.Length + (kind == "length" ? 1 : 0); return Task.FromResult(response);
        });
        var factory = new AzureTestFactory(handler) { OnClientDispose = () => { if (kind == "client-dispose") throw new IOException("client dispose"); } };
        await Assert.ThrowsAnyAsync<Exception>(() => new AzureAudioFileAdapter(factory, AzureFixture.Options()).TranscribeAsync(AzureFixture.File(upload), cancellation.Token));
        Assert.True(source.Disposed); Assert.False(upload.Disposed); Assert.Equal(1, factory.ClientDisposals); Assert.False(factory.Disposed);
    }
    /// <summary>原生上传超限或读取故障失败不回卷、重发或关闭用户流。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UploadFailureKeepsBorrowing(bool limit)
    {
        using var upload = new AzureReadStream(AzureFixture.Upload) { FailAfter = limit ? null : 3 };
        using var handler = new StubHttpHandler(async (message, token) =>
        { using var sink = new MemoryStream(); await message.Content!.CopyToAsync(sink, token); return AzureFixture.Response(AzureFixture.Result(false)); });
        var factory = new AzureTestFactory(handler);
        await Assert.ThrowsAnyAsync<Exception>(() => new AzureAudioFileAdapter(factory, AzureFixture.Options(upload: limit ? 4 : 65536)).TranscribeAsync(AzureFixture.File(upload), TestContext.Current.CancellationToken));
        Assert.False(upload.Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientDisposals);
    }
}
