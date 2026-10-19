using Xunit;
using static Asgard.Loki.LLM.Tests.WebSocketFixture;

namespace Asgard.Loki.LLM.Tests;

/// <summary>缓存读取等待期间关闭连接，不得在等待完成后继续调用原生收发。</summary>
public sealed class WebSocketCloseRaceTests
{
    /// <summary>显式关闭或释放发生后，尚未进入传输的操作不再穿透至适配器。</summary>
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task CloseDuringSessionValidation_PreventsNativeIo(bool read, bool dispose)
    {
        var provider = new StubResponsesWebSocketProvider(); var store = Store(); await using var client = Build(provider, store);
        await using var connection = await client.LLM.Responses.OpenWebSocketAsync("public", Call(), TestContext.Current.CancellationToken);
        var raw = Assert.Single(provider.Opened).Connection;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        async Task Operation()
        {
            if (read) { await foreach (var _ in connection.ReadEventsAsync(TestContext.Current.CancellationToken)) { } }
            else await connection.SendAsync(new() { ["type"] = "response.create" }, TestContext.Current.CancellationToken);
        }
        var pending = Operation(); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (dispose) await connection.DisposeAsync(); else await connection.CloseAsync(TestContext.Current.CancellationToken);
        release.TrySetResult(); var error = await Record.ExceptionAsync(() => pending);
        if (dispose) Assert.IsType<ObjectDisposedException>(error); else Assert.IsType<InvalidOperationException>(error);
        Assert.Empty(raw.Sent); Assert.Equal(0, raw.ReaderStarts);
    }
}
