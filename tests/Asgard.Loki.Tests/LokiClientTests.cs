using Xunit;

namespace Asgard.Loki.Tests;

/// <summary>验证统一入口与能力包无关的模块机制。</summary>
public sealed class LokiClientTests
{
    /// <summary>客户端延迟创建模块，并在同一客户端内保持同一实例。</summary>
    [Fact]
    public async Task GetModule_CreatesOnceAndDisposesOnce()
    {
        var creations = 0;
        var builder = new LokiClientBuilder();
        builder.AddModule(() => { creations++; return new TrackedModule(); });
        var client = builder.Build();
        Assert.Equal(0, creations);
        var module = client.GetModule<TrackedModule>();
        Assert.Same(module, client.GetModule<TrackedModule>());
        Assert.Equal(1, creations);
        await client.DisposeAsync();
        await client.DisposeAsync();
        Assert.Equal(1, module.Disposals);
        Assert.Throws<ObjectDisposedException>(() => client.GetModule<TrackedModule>());
    }

    /// <summary>入口不预先注册任何能力。</summary>
    [Fact]
    public async Task GetModule_RejectsMissingRegistration()
    {
        await using var client = new LokiClientBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => client.GetModule<TrackedModule>());
    }

    /// <summary>同一模块类型不能重复注册。</summary>
    [Fact]
    public void AddModule_RejectsDuplicateType()
    {
        var builder = new LokiClientBuilder();
        builder.AddModule(() => new TrackedModule());
        Assert.Throws<InvalidOperationException>(() => builder.AddModule(() => new TrackedModule()));
    }

    /// <summary>构建结果不受后续注册影响。</summary>
    [Fact]
    public async Task Build_SnapshotsRegistrations()
    {
        var builder = new LokiClientBuilder();
        await using var before = builder.Build();
        builder.AddModule(() => new TrackedModule());
        await using var after = builder.Build();
        Assert.Throws<InvalidOperationException>(() => before.GetModule<TrackedModule>());
        Assert.NotNull(after.GetModule<TrackedModule>());
    }
}
