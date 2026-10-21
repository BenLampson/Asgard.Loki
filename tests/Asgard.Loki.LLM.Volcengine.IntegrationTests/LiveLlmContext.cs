using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>组合真实 HTTP、内存会话和进程内测试协调器，不代表 Redis 多实例验证。</summary>
internal sealed class LiveLlmContext : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly LiveConfiguration _configuration;
    private readonly TestSessionLock _sessionLock = new();
    /// <summary>真实调用的安全审计与请求预算。</summary>
    internal LiveHttpHandler Handler { get; }

    /// <summary>默认标准方舟地址，也允许测试代码显式覆盖目标地址。</summary>
    internal LiveLlmContext(LiveConfiguration configuration, Uri? baseAddress = null)
    {
        _configuration = configuration;
        BaseAddress = baseAddress ?? LiveConfiguration.DefaultBaseAddress;
        Handler = new LiveHttpHandler(configuration.ApiKey);
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddHttpClient("Asgard.Loki.LLM.Volcengine", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        _services = services.BuildServiceProvider();
    }

    /// <summary>本次场景使用的完整 API 根地址。</summary>
    internal Uri BaseAddress { get; }

    /// <summary>重建客户端时仍然借用同一缓存与协调器，以验证会话恢复。</summary>
    internal ILokiClient Build()
    {
        var provider = new VolcengineLlmProvider(_services.GetRequiredService<IHttpClientFactory>());
        LlmTarget Target(string id, string model, LlmProtocol protocol) => new()
        {
            Id = id, ProviderId = provider.Id, BaseAddress = BaseAddress,
            UpstreamModel = model, CredentialId = "live-key", Protocols = protocol
        };
        return new LokiClientBuilder().AddLLM(new LlmOptions
        {
            SessionStore = new DistributedCacheLlmSessionStore(_services.GetRequiredService<IDistributedCache>(), _sessionLock),
            Credentials = [new("live-key", _configuration.ApiKey)], Providers = [provider],
            Routes =
            [
                new LlmRoute { Model = "live-chat", Targets = [Target("ark-chat", _configuration.ChatModel, LlmProtocol.ChatCompletions)] },
                new LlmRoute { Model = "live-responses", Targets = [Target("ark-responses", _configuration.ResponsesModel, LlmProtocol.Responses)] }
            ]
        }).Build();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
