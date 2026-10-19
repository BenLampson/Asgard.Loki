using System.Text.Json;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>调用者原生适配器的异步枚举、释放和取消必须在转换终态之前被完整观察。</summary>
public sealed class BedrockStreamOwnershipTests
{
    /// <summary>即使源已交付逻辑停止，异步释放失败/取消仍不能产生转换成功。</summary>
    [Theory] [InlineData(true, "success")] [InlineData(false, "success")]
    [InlineData(true, "dispose")] [InlineData(false, "dispose")]
    [InlineData(true, "cancel")] [InlineData(false, "cancel")]
    public async Task AsyncSourceDisposal_IsObservedBeforeConvertedTerminal(bool nativeNova, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var native = new BedrockAsyncStreamProvider(nativeNova) { FailDispose = kind == "dispose", OnDispose = kind == "cancel" ? cancellation.Cancel : null };
        await using var client = Client(native, !nativeNova); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (var item in Events(client, !nativeNova, cancellation.Token))
            { if (item.Type is "messageStop" or "metadata") Assert.True(native.Disposed); seen.Add(item); }
        });
        if (kind == "success") { Assert.Null(failure); Assert.Equal(15, seen.Count); }
        else
        {
            if (kind == "dispose") Assert.IsType<IOException>(failure); else Assert.IsAssignableFrom<OperationCanceledException>(failure);
            Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata"); Assert.Equal(13, seen.Count);
        }
        Assert.True(native.Disposed); Assert.Equal(1, native.Calls);
    }

    /// <summary>提前退出仅释放本次异步枚举，不继续消费、补终态或重新订阅。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task EarlyExit_DisposesOnlyCurrentSource(bool nativeNova)
    {
        var native = new BedrockAsyncStreamProvider(nativeNova); await using var client = Client(native, !nativeNova); List<LlmJsonEvent> seen = [];
        await foreach (var item in Events(client, !nativeNova, TestContext.Current.CancellationToken)) { seen.Add(item); break; }
        Assert.Equal("messageStart", Assert.Single(seen).Type); Assert.True(native.Disposed); Assert.Single(native.Created); Assert.Equal(1, native.Calls);
    }

    /// <summary>尾部交付之间再次检查取消，不能在已经取消之后继续发出用量事件。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task CancellationBetweenTerminalEvents_SuppressesRemainingDelivery(bool nativeNova)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var native = new BedrockAsyncStreamProvider(nativeNova); await using var client = Client(native, !nativeNova); List<LlmJsonEvent> seen = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in Events(client, !nativeNova, cancellation.Token))
            { seen.Add(item); if (item.Type == "messageStop") { Assert.True(native.Disposed); cancellation.Cancel(); } }
        });
        Assert.Equal("messageStop", seen[^1].Type); Assert.DoesNotContain(seen, item => item.Type == "metadata"); Assert.Equal(1, native.Calls);
    }

    /// <summary>调用前取消不启动应用原生源，也不依赖原生源去完成第一次取消检查。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task PreCancelledCall_DoesNotEnumerateSource(bool nativeNova)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancellation.Cancel();
        var native = new BedrockAsyncStreamProvider(nativeNova); await using var client = Client(native, !nativeNova);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in Events(client, !nativeNova, cancellation.Token)) { } });
        Assert.Equal(0, native.Calls); Assert.Empty(native.Created);
    }

    /// <summary>同协议身份流返回原始事件对象，不能因为增加转换流而替换或重建供应商附件宿主。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task NativeIdentityStream_ReturnsOriginalEventInstances(bool nova)
    {
        var native = new BedrockAsyncStreamProvider(nova); await using var client = Client(native, nova); List<LlmJsonEvent> seen = [];
        await foreach (var item in Events(client, nova, TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Equal(15, seen.Count); for (var i = 0; i < seen.Count; i++) { Assert.Same(native.Created[i], seen[i]); Assert.Null(seen[i].Metadata.Conversion); }
        Assert.True(native.Disposed); Assert.Equal(1, native.Calls);
    }

    /// <summary>缓存与首次绑定协调均为本地测试实现，原生适配器无 HTTP 或外部依赖。</summary>
    private static ILokiClient Client(BedrockAsyncStreamProvider native, bool publicNova)
    {
        var protocol = native.NativeNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse;
        var provider = new ConvertingBedrockLlmProvider(native, protocol, "explicit", BedrockConversionFixtures.Options());
        return new LokiClientBuilder().AddLLM(new()
        {
            Providers = [provider], Credentials = [new("identity", "fixture-key")],
            SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
            Routes = [new() { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://stub.invalid/"),
                UpstreamModel = "model-a", CredentialId = "identity", Protocols = publicNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse }] }]
        }).Build();
    }
    /// <summary>只使用公共 SDK 的两个原生流入口。</summary>
    private static async IAsyncEnumerable<LlmJsonEvent> Events(ILokiClient client, bool nova,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var request = BedrockConversionFixtures.Read("v2", nova ? "Nova" : "ConverseDirect");
        if (nova) { await foreach (var item in client.LLM.BedrockNova.StreamAsync(request.Deserialize<BedrockNovaRequest>()!, new() { SessionId = "async-source" }, token)) yield return item; }
        else { await foreach (var item in client.LLM.BedrockConverse.StreamAsync(request.Deserialize<BedrockConverseRequest>()!, new() { SessionId = "async-source" }, token)) yield return item; }
    }
}
