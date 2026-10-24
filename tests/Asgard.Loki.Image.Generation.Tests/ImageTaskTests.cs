using System.Text.Json;
using Asgard.Loki.Image.Ali;
using Asgard.Loki.Image.OpenAI;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.Generation.Tests;

/// <summary>异步生成的显式状态及可持久化完整目标引用，不执行自动查询。</summary>
public sealed class ImageTaskTests
{
    private const string Pending = """{ "output":{"task_id":"task-one","task_status":"PENDING"},"request_id":"fixture" }""";
    private const string Completed = """{ "output":{"task_id":"task-one","task_status":"SUCCEEDED","results":[{"url":"https://cdn.example.invalid/a.png"}]} }""";

    /// <summary>一次提交不轮询；序列化后可重建客户端查询同一上游，目标重排不改变绑定。</summary>
    [Fact]
    public async Task TaskReference_RoundTripsAndPinsOriginalProvider()
    {
        using var http = new StubHttpHandler((message, _) =>
        {
            Assert.Equal("one.example.invalid", message.RequestUri!.Host);
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/services/aigc/image-generation/generation", message.RequestUri.AbsolutePath);
                Assert.Equal("enable", message.Headers.GetValues("X-DashScope-Async").Single());
                return Task.FromResult(GenerationFixture.Response(Pending));
            }
            Assert.Equal("/v1/tasks/task-one", message.RequestUri.AbsolutePath);
            return Task.FromResult(GenerationFixture.Response(Completed));
        });
        var target = GenerationFixture.Target(new AliImageTaskGenerationHandler());
        var other = GenerationFixture.Target(new OpenAiImageGenerationHandler(), "two");
        var transport = new GenerationHttpFactory(http);
        var submitted = await GenerationFixture.Client(transport, target, other).GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(LokiImageGenerationState.Pending, submitted.State); Assert.Empty(submitted.Images); Assert.Equal(1, http.Calls);
        var json = JsonSerializer.Serialize(submitted.TaskReference);
        Assert.DoesNotContain(target.ApiKey, json);
        var reference = JsonSerializer.Deserialize<LokiImageTaskReference>(json)!;
        var completed = await GenerationFixture.Client(transport, other, target).GetAsync(reference, TestContext.Current.CancellationToken);
        Assert.Equal(LokiImageGenerationState.Succeeded, completed.State); Assert.Single(completed.Images);
        Assert.Equal(reference.TaskId, completed.TaskReference!.TaskId); Assert.Equal(2, http.Calls);
        Assert.Equal(Completed, System.Text.Encoding.UTF8.GetString(completed.RawResponse!.Body.Span));
    }

    /// <summary>改变服务器、实际模型、凭据、HTTP 策略或 Handler 后不能向新目标查询旧任务。</summary>
    [Theory]
    [InlineData("key")] [InlineData("model")] [InlineData("address")] [InlineData("handler")] [InlineData("http-client")] [InlineData("removed")]
    public async Task ChangedTarget_FailsBeforeQuery(string kind)
    {
        using var http = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(Pending)));
        var target = GenerationFixture.Target(new AliImageTaskGenerationHandler());
        var transport = new GenerationHttpFactory(http);
        var submitted = await GenerationFixture.Client(transport, target).GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        var changed = kind switch
        {
            "key" => target with { ApiKey = "different-fixture-key" },
            "model" => target with { UpstreamModel = "other" },
            "address" => target with { BaseAddress = new Uri("https://other.example.invalid/v1/") },
            "handler" => target with { Handler = new AliImageGenerationHandler() },
            "http-client" => target with { HttpClientName = "other-policy" },
            _ => target with { Id = "removed" }
        };
        var client = GenerationFixture.Client(transport, changed);
        var error = await Record.ExceptionAsync(async () => await client.GetAsync(submitted.TaskReference!, TestContext.Current.CancellationToken));
        Assert.IsAssignableFrom<Exception>(error); Assert.Equal(1, http.Calls);
    }

    /// <summary>HTTP 200、输出 URL 或未知原生状态都不能被当成生成成功。</summary>
    [Theory]
    [InlineData("RUNNING", LokiImageGenerationState.Running)]
    [InlineData("FUTURE", LokiImageGenerationState.Unknown)]
    [InlineData("FAILED", LokiImageGenerationState.Failed)]
    [InlineData("CANCELED", LokiImageGenerationState.Canceled)]
    public async Task NativeTaskStates_AreNotInferredFromUrls(string status, LokiImageGenerationState expected)
    {
        using var http = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(
            """{"output":{"task_id":"task-one","task_status":"STATUS","results":[{"url":"https://cdn.example.invalid/partial.png"}],"code":"fixture"}}"""
                .Replace("STATUS", status, StringComparison.Ordinal))));
        var result = await GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(new AliImageTaskGenerationHandler()))
            .GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.State); Assert.Single(result.Images); Assert.NotNull(result.TaskReference);
        Assert.Equal("fixture", result.Data.GetProperty("output").GetProperty("code").GetString());
        Assert.Equal(1, http.Calls);
    }

    /// <summary>原生查询返回错误任务 ID 时严格失败。</summary>
    [Fact]
    public async Task WrongNativeTaskId_IsRejected()
    {
        using var http = new StubHttpHandler((message, _) => Task.FromResult(GenerationFixture.Response(
            message.Method == HttpMethod.Post ? Pending : Completed.Replace("task-one", "task-other", StringComparison.Ordinal))));
        var client = GenerationFixture.Client(new GenerationHttpFactory(http), GenerationFixture.Target(new AliImageTaskGenerationHandler()));
        var result = await client.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await client.GetAsync(result.TaskReference!, TestContext.Current.CancellationToken));
        Assert.Equal(2, http.Calls);
    }
}
