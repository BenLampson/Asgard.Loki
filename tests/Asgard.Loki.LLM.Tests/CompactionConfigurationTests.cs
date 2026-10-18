using System.Text.Json;
using Asgard.Loki.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>可选操作的配置必须有实际原生适配，且不能改变旧基础目标的默认序列化。</summary>
public sealed class CompactionConfigurationTests
{
    /// <summary>未知操作位、缺失协议或缺失可选实现都在注册阶段失败。</summary>
    [Theory]
    [InlineData(2, LlmProtocol.Responses)]
    [InlineData(-1, LlmProtocol.Responses)]
    [InlineData(1, LlmProtocol.ChatCompletions)]
    [InlineData(1, LlmProtocol.Responses)]
    public void UnimplementedFeatures_AreRejected(int flags, LlmProtocol protocol)
    {
        var target = Target() with { Features = (LlmTargetFeatures)flags, Protocols = protocol };
        var options = new LlmOptions
        {
            SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
            Providers = [new StubLlmProvider()], Credentials = [new("key", "test-only")],
            Routes = [new LlmRoute { Model = "public", Targets = [target] }]
        };
        Assert.Throws<ArgumentException>(() => new LokiClientBuilder().AddLLM(options));
    }

    /// <summary>新增可选功能默认省略，保持历史基础目标指纹使用的 JSON 字节结构。</summary>
    [Fact]
    public void DefaultFeatures_PreserveLegacyTargetSerialization()
    {
        var target = Target();
        var legacy = new { target.Id, target.ProviderId, target.BaseAddress, target.UpstreamModel, target.CredentialId, target.Version, target.Protocols };
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(target));
        using var enabled = JsonDocument.Parse(JsonSerializer.Serialize(target with { Features = LlmTargetFeatures.ResponsesCompaction }));
        Assert.Equal(1, enabled.RootElement.GetProperty("Features").GetInt32());
    }

    /// <summary>旧调用者实现仍可编译使用，新方法明确报告不支持，而不是把压缩伪装成创建响应。</summary>
    [Fact]
    public async Task LegacyImplementation_ReportsOptionalOperationUnsupported()
    {
        IResponsesClient client = new LegacyResponsesClient();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CompactAsync(new() { Model = "public" }, new() { SessionId = "legacy" }, TestContext.Current.CancellationToken));
    }

    /// <summary>先加入双工扩展契约也不能让旧客户端或基础 HTTP 适配器隐式宣称可连接。</summary>
    [Fact]
    public async Task LegacyImplementation_ReportsWebSocketUnsupported()
    {
        IResponsesClient client = new LegacyResponsesClient();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.OpenWebSocketAsync("public", new() { SessionId = "legacy" }, TestContext.Current.CancellationToken));
        Assert.False((ILlmProvider)new StubLlmProvider() is IResponsesWebSocketAdapter);
    }

    /// <summary>纯基础目标，供应商故意只实现原有 Chat/Responses 接口。</summary>
    private static LlmTarget Target() => new()
    {
        Id = "target", ProviderId = "stub", BaseAddress = new("https://target.invalid/v1/"), UpstreamModel = "model", CredentialId = "key"
    };
}
