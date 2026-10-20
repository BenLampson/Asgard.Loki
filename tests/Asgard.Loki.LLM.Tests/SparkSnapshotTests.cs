using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Asgard.Loki.LLM.Tests.SparkCoreTests;

namespace Asgard.Loki.LLM.Tests;

/// <summary>原生嵌套路由路径和完整参数快照在缓存/目标等待之前确定，不覆盖调用者业务字段。</summary>
public sealed class SparkSnapshotTests
{
    /// <summary>完整/流式请求均在首次实际异步缓存等待前冻结整个原生正文。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task SnapshotPrecedesAsynchronousCacheRead(bool stream)
    {
        using var provider = new SparkStubProvider(); var config = Configuration(provider); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledSessionStore(config.SessionStore!) { BeforeRead = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); } }; await using var client = new LokiClientBuilder().AddLLM(config with { SessionStore = store }).Build(); var request = Request(); var token = TestContext.Current.CancellationToken;
        async Task Invoke() { if (stream) await foreach (var _ in client.LLM.Spark.StreamAsync(request, new() { SessionId = "wait" }, token)) { } else await client.LLM.Spark.CompleteAsync(request, new() { SessionId = "wait" }, token); }
        var call = Invoke(); await entered.Task.WaitAsync(token); request.Parameters.Clear(); release.SetResult(); await call;
        Assert.True(JsonElement.DeepEquals(SparkStubProvider.Part("core_wire"), JsonSerializer.SerializeToElement(Assert.Single(provider.Calls).Body))); Assert.Equal(1, store.GetOrCreateCalls);
    }
    /// <summary>非法原生控制字段或 Unicode/路径形状错误先于缓存与调度失败。</summary>
    [Theory] [InlineData("model")] [InlineData("MODEL")] [InlineData("stream")] [InlineData("STREAM")]
    [InlineData("domain")] [InlineData("DOMAIN")] [InlineData("app_id")] [InlineData("APP_ID")]
    [InlineData("root-case")] [InlineData("chat-case")] [InlineData("header-null")] [InlineData("parameter-null")] [InlineData("chat-null")]
    [InlineData("header-scalar")] [InlineData("parameter-scalar")] [InlineData("undefined")]
    [InlineData("unicode-model")] [InlineData("unicode-key")] [InlineData("unicode-value")]
    public async Task InvalidNativeRequestsNeverReachBindingOrAdapter(string kind)
    {
        foreach (var stream in new[] { false, true })
        {
            using var provider = new SparkStubProvider(); var config = Configuration(provider); var selector = new ModerationTestSelector(); var reads = 0; var store = new ControlledSessionStore(config.SessionStore!) { BeforeRead = _ => { Interlocked.Increment(ref reads); return Task.CompletedTask; } }; await using var client = new LokiClientBuilder().AddLLM(config with { SessionStore = store, TargetSelector = selector }).Build(); var request = Request();
            if (kind is "model" or "MODEL" or "stream" or "STREAM") request.Parameters[kind] = JsonSerializer.SerializeToElement(false);
            else if (kind is "domain" or "DOMAIN" or "chat-case" or "chat-null")
            {
                var parameter = JsonNode.Parse(request.Parameters["parameter"].GetRawText())!.AsObject();
                if (kind == "chat-case") parameter["CHAT"] = new JsonObject(); else if (kind == "chat-null") parameter["chat"] = null; else parameter["chat"]![kind] = "lite";
                request.Parameters["parameter"] = JsonSerializer.SerializeToElement(parameter);
            }
            else if (kind is "app_id" or "APP_ID") { var header = JsonNode.Parse(request.Parameters["header"].GetRawText())!.AsObject(); header[kind] = "fixture-app"; request.Parameters["header"] = JsonSerializer.SerializeToElement(header); }
            else if (kind == "root-case") request.Parameters["Parameter"] = JsonSerializer.SerializeToElement(new { });
            else if (kind.EndsWith("-null", StringComparison.Ordinal)) request.Parameters[kind[..^5]] = JsonSerializer.SerializeToElement<object?>(null);
            else if (kind.EndsWith("-scalar", StringComparison.Ordinal)) request.Parameters[kind[..^7]] = JsonSerializer.SerializeToElement(false);
            else if (kind == "undefined") request.Parameters["future"] = default;
            else if (kind == "unicode-model") request = request with { Model = new string((char)0xd800, 1) };
            else if (kind == "unicode-key") request.Parameters[new string((char)0xd800, 1)] = JsonSerializer.SerializeToElement(false);
            else if (kind == "unicode-value") request.Parameters["future"] = JsonSerializer.Deserialize<JsonElement>("\"\\ud800\"");
            var error = await Record.ExceptionAsync(async () => { if (stream) await foreach (var _ in client.LLM.Spark.StreamAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken)) { } else await client.LLM.Spark.CompleteAsync(request, new() { SessionId = "invalid" }, TestContext.Current.CancellationToken); });
            Assert.NotNull(error); Assert.Equal(0, reads); Assert.Equal(0, store.GetOrCreateCalls); Assert.Equal(0, selector.Calls); Assert.Empty(provider.Calls);
        }
    }
    /// <summary>只创建拥有路由意义的 parameter/chat/domain，不补 AppId、消息、默认设置或 stream。</summary>
    [Fact]
    public async Task MissingNativeBodyDoesNotBecomeGeneratedMessagesOrParameters()
    {
        using var provider = new SparkStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); await client.LLM.Spark.CompleteAsync(new() { Model = "public-spark" }, new() { SessionId = "minimal" }, TestContext.Current.CancellationToken);
        var body = Assert.Single(provider.Calls).Body; Assert.Equal("{\"parameter\":{\"chat\":{\"domain\":\"lite\"}}}", body.ToJsonString());
    }
    /// <summary>未知嵌套重复属性不通过映射被压成最后一个值；原生供应商可以显式拒绝歧义输入。</summary>
    [Fact]
    public async Task UnknownNestedDuplicateFieldsRemainUnmodified()
    {
        using var provider = new SparkStubProvider(); await using var client = new LokiClientBuilder().AddLLM(Configuration(provider)).Build(); var request = Request(); request.Parameters["future"] = JsonSerializer.Deserialize<JsonElement>("{\"value\":1,\"value\":2}");
        await client.LLM.Spark.CompleteAsync(request, new() { SessionId = "duplicate" }, TestContext.Current.CancellationToken); var data = JsonSerializer.SerializeToElement(Assert.Single(provider.Calls).Body).GetProperty("future"); Assert.Equal(new[] { 1, 2 }, data.EnumerateObject().Select(p => p.Value.GetInt32()));
    }
}
