using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>自定义处理器、取消和非法选择结果的公共边界。</summary>
public sealed class ImageExtensionTests
{
    /// <summary>应用处理器修改调用上下文不能导致不可重建的目标绑定被当作成功返回。</summary>
    [Fact]
    public async Task HandlerCannotMutateTargetDuringInvocation()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http),
            GenerationFixture.Target(new CustomImageHandler { MutateTarget = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken));
    }
    /// <summary>自定义单例 Handler 可以直接交付二进制，不要求先伪造 Base64 或网络请求。</summary>
    [Fact]
    public async Task CustomHandler_CanReturnDirectBinary()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http);
        var client = GenerationFixture.Client(transport, GenerationFixture.Target(new CustomImageHandler()));
        var result = await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        var content = await client.GetImageContentAsync(Assert.Single(result.Images), TestContext.Current.CancellationToken);
        Assert.Equal("image/png", content.ContentType); Assert.Equal(8, content.Bytes.Length);
        Assert.Equal(LokiImageGenerationState.Succeeded, result.State);
        Assert.Null(result.RawResponse); Assert.True(result.Data.GetProperty("custom").GetBoolean());
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>自定义 Handler 不能把其他上游的结果交付为当前目标结果。</summary>
    [Fact]
    public async Task WrongProviderResult_IsRejected()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http),
            GenerationFixture.Target(new CustomImageHandler { WrongProviderId = "other" }));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>异步状态必须附带原生任务 ID。</summary>
    [Theory]
    [InlineData(LokiImageGenerationState.Pending)] [InlineData(LokiImageGenerationState.Running)]
    public async Task AsyncResultWithoutTaskId_IsRejected(LokiImageGenerationState state)
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http),
            GenerationFixture.Target(new CustomImageHandler { State = state }));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken));
    }

    /// <summary>只有明确提供查询契约的 Handler 才可执行任务查询。</summary>
    [Fact]
    public async Task MissingQueryContract_IsExplicitlyUnsupported()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http),
            GenerationFixture.Target(new CustomImageHandler { ReturnTaskId = true }));
        var result = await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await client.GetAsync(result.TaskReference!, TestContext.Current.CancellationToken));
    }

    /// <summary>选择器的非法索引不能被取模或默认目标掩盖。</summary>
    [Fact]
    public async Task InvalidSelector_DoesNotDispatch()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http);
        var client = new LokiImageFactory(new()
        { Models = [new() { Name = "pictures", Providers = [GenerationFixture.Target()], Selector = new InvalidImageSelector() }] }, transport).CreateClient();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>已经取消的生成和内容请求不触发选择或 HTTP 创建。</summary>
    [Fact]
    public async Task CanceledCalls_DoNotCreateHttpClient()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http); var client = GenerationFixture.Client(transport);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.GenerateAsync(GenerationFixture.Request(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.GetImageContentAsync(null!, cancellation.Token));
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>全失败图片保留原始错误，不标记为成功。</summary>
    [Fact]
    public async Task AllImageErrors_AreFailedResult()
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(new CustomImageHandler { FailImage = true }));
        var result = await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(LokiImageGenerationState.Failed, result.State); Assert.NotNull(Assert.Single(result.Images).Error);
    }
}
