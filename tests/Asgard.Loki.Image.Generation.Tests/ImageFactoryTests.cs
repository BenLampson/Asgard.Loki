using System.Text.Json;
using Asgard.Loki.Image.OpenAI;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>模型注册、选择、配置快照和并发完整目标绑定。</summary>
public sealed class ImageFactoryTests
{
    /// <summary>同一个单例 Handler 可复用在官方和中转站目标，不混用模型和凭据。</summary>
    [Fact]
    public async Task ConcurrentCalls_KeepCompleteTargets_AndIndependentClientCounters()
    {
        using var http = new StubHttpHandler(async (request, token) =>
        {
            await Task.Yield();
            var id = request.RequestUri!.Host.Split('.')[0];
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal("native-" + id, body.RootElement.GetProperty("model").GetString());
            Assert.Equal("fixture-key-" + id, request.Headers.Authorization!.Parameter);
            Assert.Equal("/v1/images/generations", request.RequestUri.AbsolutePath);
            return GenerationFixture.Response();
        });
        var transport = new GenerationHttpFactory(http); var singleton = new OpenAiImageGenerationHandler();
        var factory = new LokiImageFactory(GenerationFixture.Registration(
            GenerationFixture.Target(singleton, "one"), GenerationFixture.Target(singleton, "two")), transport);
        var client = factory.CreateClient();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(20, results.Count(result => result.ProviderId == "one"));
        Assert.Equal(20, results.Count(result => result.ProviderId == "two"));
        Assert.Equal("one", (await factory.CreateClient().GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken)).ProviderId);
        Assert.Equal(41, http.Calls);
    }

    /// <summary>处理器名字在 Factory 创建时解析；后续修改注册列表、规则和 JSON 不影响调用。</summary>
    [Fact]
    public async Task RegistrationAndRequest_AreFrozenBeforeDispatch()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new StubHttpHandler(async (request, token) =>
        {
            await gate.Task.WaitAsync(token);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(1, body.RootElement.GetProperty("n").GetInt32());
            Assert.Equal(9007199254740993L, body.RootElement.GetProperty("future").GetProperty("exact").GetInt64());
            return GenerationFixture.Response();
        });
        var rules = GenerationFixture.Rules();
        var targets = new List<LokiImageUpstreamProvider> { GenerationFixture.Target() with { Handler = null, HandlerName = "shared", Parameters = rules } };
        var handlers = new Dictionary<string, ILokiImageGenerationHandler> { ["shared"] = new OpenAiImageGenerationHandler() };
        var registration = new LokiImageProvider { Handlers = handlers, Models = [new() { Name = "pictures", Providers = targets }] };
        var client = new LokiImageFactory(registration, new GenerationHttpFactory(http)).CreateClient();
        targets.Clear(); handlers.Clear(); rules.Clear();
        var input = GenerationFixture.Request(); var operation = client.GenerateAsync(input, TestContext.Current.CancellationToken);
        input.NativeParameters["future"]!["exact"] = 0; gate.SetResult();
        var result = await operation;
        Assert.Equal("one", result.ProviderId); Assert.Equal(1, http.Calls);
    }

    /// <summary>默认轮询分别属于每个公开模型，不在不同路由之间共享游标。</summary>
    [Fact]
    public async Task Models_HaveIndependentSelectionSequences()
    {
        using var http = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response()));
        var targets = new[] { GenerationFixture.Target(id: "one"), GenerationFixture.Target(id: "two") };
        var client = new LokiImageFactory(new()
        { Models = [new() { Name = "a", Providers = targets }, new() { Name = "b", Providers = targets }] }, new GenerationHttpFactory(http)).CreateClient();
        var ids = new List<string>();
        foreach (var name in new[] { "a", "b", "a", "b" })
            ids.Add((await client.GenerateAsync(GenerationFixture.Request() with { Model = name }, TestContext.Current.CancellationToken)).ProviderId);
        Assert.Equal(new[] { "one", "one", "two", "two" }, ids);
    }

    /// <summary>注册表的缺项、重复、歧义和非法目标在联网前失败。</summary>
    [Theory]
    [InlineData("empty")] [InlineData("duplicate-model")] [InlineData("empty-targets")] [InlineData("duplicate-id")]
    [InlineData("missing-handler")] [InlineData("unknown-handler")] [InlineData("both-handlers")] [InlineData("address")]
    [InlineData("range")] [InlineData("null-rule")]
    public void InvalidRegistration_FailsWithoutCreatingHttpClient(string kind)
    {
        using var http = new StubHttpHandler((_, _) => throw new InvalidOperationException("No IO"));
        var transport = new GenerationHttpFactory(http); var target = GenerationFixture.Target();
        target = kind switch
        {
            "missing-handler" => target with { Handler = null },
            "unknown-handler" => target with { Handler = null, HandlerName = "absent" },
            "both-handlers" => target with { HandlerName = "duplicate" },
            "address" => target with { BaseAddress = new Uri("https://user:password@example.invalid/v1/") },
            "range" => target with { Parameters = new Dictionary<string, LokiImageParameterRule> { ["Count"] = new() { Minimum = 3, Maximum = 1 } } },
            "null-rule" => target with { Parameters = new Dictionary<string, LokiImageParameterRule> { ["Count"] = null! } },
            _ => target
        };
        var model = new LokiImageModel { Name = "pictures", Providers = kind == "empty-targets" ? [] : kind == "duplicate-id" ? [target, target] : [target] };
        var registration = new LokiImageProvider { Models = kind == "empty" ? [] : kind == "duplicate-model" ? [model, model] : [model] };
        Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new LokiImageFactory(registration, transport)));
        Assert.Equal(0, transport.Calls);
    }

    /// <summary>凭据既不出现在默认序列化结果，也不出现在目标诊断字符串中。</summary>
    [Fact]
    public void Provider_DoesNotExposeKeyInJsonOrToString()
    {
        var provider = GenerationFixture.Target();
        Assert.DoesNotContain(provider.ApiKey, JsonSerializer.Serialize(provider));
        Assert.DoesNotContain(provider.ApiKey, provider.ToString());
    }

    /// <summary>旧接口、原生适配器和统一入口可以在同一 Loki 实例注册且互不抢占。</summary>
    [Fact]
    public async Task ExistingAndUnifiedGeneration_Coexist()
    {
        using var http = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response()));
        var transport = new GenerationHttpFactory(http);
        var adapter = new OpenAiImageAdapter(transport, new() { Model = "legacy-native", ApiKey = "fixture-legacy-key" });
        await using var loki = new LokiClientBuilder().AddImage(new ImageOptions
        {
            GenerationFactory = new(GenerationFixture.Registration(GenerationFixture.Target()), transport),
            Routes = [new() { Model = "legacy", Targets = [adapter] }]
        }).Build();
        // 保留目标类型 new() 的现有代码仍可编译，不引入 GenerateAsync 重载歧义。
        var legacy = await loki.Image.GenerateAsync(new() { Model = "legacy", Prompt = "cat" }, TestContext.Current.CancellationToken);
        var unified = await loki.Image.Generation.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal("legacy-native", legacy.Metadata.UpstreamModel);
        Assert.Equal("native-one", unified.UpstreamModel); Assert.Equal(2, http.Calls);
    }
}
