using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Ali.Tests;

/// <summary>异步提交路径、任务关联、状态和部分产物的独立离线契约。</summary>
public sealed class AliImageTaskTests
{
    /// <summary>三种显式提交服务与一次查询保持完整原生载荷。</summary>
    [Theory]
    [InlineData(AliImageTaskOperation.ImageGeneration, "image-generation/generation", 0)]
    [InlineData(AliImageTaskOperation.TextToImage, "text2image/image-synthesis", 1)]
    [InlineData(AliImageTaskOperation.ImageToImage, "image2image/image-synthesis", 2)]
    public async Task EachNativeSubmitAndSingleGetKeepOriginalBodies(AliImageTaskOperation operation, string path, int input)
    {
        using var factory = new AliTestFactory(); var request = AliFixture.Request(input); var reply = AliFixture.Part("tasks");
        if (input == 0) request.Parameters["parameters"]!["enable_interleave"] = true;
        factory.OnSend = async (message, _) =>
        {
            if (message.Method == HttpMethod.Post)
            {
                Assert.EndsWith("services/aigc/" + path, message.RequestUri!.AbsoluteUri);
                Assert.Equal("enable", Assert.Single(message.Headers.GetValues("X-DashScope-Async"))); Assert.False(message.Headers.Contains("X-DashScope-SSE"));
                var body = AliFixture.Element(await message.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken)); var expected = request.Parameters.DeepClone().AsObject(); expected["model"] = "native-fixture-model";
                Assert.True(JsonElement.DeepEquals(AliFixture.Element(expected.ToJsonString()), body)); return AliFixture.Json(reply[0]);
            }
            Assert.Equal("https://ali-image.example.invalid/workspace/api/v1/tasks/task%2Ffixture%3Fx%3D1", message.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-ali-secret", message.Headers.Authorization!.Parameter); Assert.Null(message.Content); Assert.False(message.Headers.Contains("X-DashScope-Async"));
            return AliFixture.Json(reply[1]);
        };
        var client = new AliImageTaskClient(factory, AliFixture.Options(), operation); var submitted = await client.SubmitAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("PENDING", submitted.Status); Assert.False(submitted.IsTerminal); Assert.True(JsonElement.DeepEquals(reply[0], submitted.Data));
        var result = await client.GetAsync(submitted.Reference, TestContext.Current.CancellationToken);
        Assert.True(result.IsTerminal); Assert.Equal("SUCCEEDED", result.Status); Assert.Single(result.Images); Assert.True(JsonElement.DeepEquals(reply[1], result.Data));
        Assert.Equal(2, factory.Calls); Assert.Equal(2, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>未知状态不推测终态，失败及部分产物仍可读取。</summary>
    [Theory]
    [InlineData("PENDING", false)] [InlineData("RUNNING", false)] [InlineData("SUCCEEDED", true)]
    [InlineData("FAILED", true)] [InlineData("CANCELED", true)] [InlineData("UNKNOWN", false)] [InlineData("FUTURE", false)]
    public async Task StatusIsNativeAndDoesNotDropFailureOrPartialResults(string status, bool terminal)
    {
        using var factory = new AliTestFactory(); var data = JsonNode.Parse(AliFixture.Part("tasks")[2].GetRawText())!.AsObject(); data["output"]!["task_status"] = status;
        factory.OnSend = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Element(data.ToJsonString())));
        var result = await new AliImageTaskClient(factory, AliFixture.Options(), AliImageTaskOperation.ImageGeneration).SubmitAsync(AliFixture.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(status, result.Status); Assert.Equal(terminal, result.IsTerminal); Assert.Single(result.Images); Assert.Equal("InputDataError", result.Data.GetProperty("output").GetProperty("code").GetString());
    }
    /// <summary>完整目标任一关键身份变化均在网络调用前拒绝。</summary>
    [Theory]
    [InlineData("key")] [InlineData("model")] [InlineData("root")] [InlineData("operation")] [InlineData("label")]
    public async Task CompleteTargetMismatchFailsBeforeNetwork(string change)
    {
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Part("tasks")[0])) };
        var original = new AliImageTaskClient(factory, AliFixture.Options(), AliImageTaskOperation.ImageGeneration);
        var task = await original.SubmitAsync(AliFixture.Request(), TestContext.Current.CancellationToken);
        var options = AliFixture.Options(key: change == "key" ? "different" : "fixture-ali-secret", model: change == "model" ? "other" : "native-fixture-model",
            root: change == "root" ? "https://another.example.invalid/api/v1/" : "https://ali-image.example.invalid/workspace/api/v1/");
        var other = new AliImageTaskClient(factory, options, change == "operation" ? AliImageTaskOperation.TextToImage : AliImageTaskOperation.ImageGeneration);
        var reference = change == "label" ? task.Reference with { PublicModel = "other" } : task.Reference;
        Assert.Throws<ArgumentException>(() => { _ = other.GetAsync(reference, TestContext.Current.CancellationToken); }); Assert.Equal(1, factory.Calls);
    }
    /// <summary>查询必须关联原任务并带明确原生状态。</summary>
    [Theory]
    [InlineData("different", "SUCCEEDED")]
    [InlineData("task/fixture?x=1", "")]
    public async Task QueryRejectsWrongTaskOrMissingStatus(string id, string status)
    {
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Part("tasks")[0])) };
        var client = new AliImageTaskClient(factory, AliFixture.Options(), AliImageTaskOperation.ImageGeneration);
        var task = await client.SubmitAsync(AliFixture.Request(), TestContext.Current.CancellationToken);
        factory.OnSend = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Element(JsonSerializer.Serialize(new { output = new { task_id = id, task_status = status } }))));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync(task.Reference, TestContext.Current.CancellationToken)); Assert.Equal(2, factory.Calls);
    }
    /// <summary>旧版结果与无状态提交确认保持原生结构。</summary>
    [Fact]
    public async Task LegacyResultAndStatuslessSubmitStayNative()
    {
        using var factory = new AliTestFactory { OnSend = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Element("""{"output":{"task_id":"task/fixture?x=1"}}"""))) };
        var client = new AliImageTaskClient(factory, AliFixture.Options(), AliImageTaskOperation.TextToImage);
        var task = await client.SubmitAsync(AliFixture.Request(1), TestContext.Current.CancellationToken); Assert.Null(task.Status); Assert.False(task.IsTerminal);
        factory.OnSend = (_, _) => Task.FromResult(AliFixture.Json(AliFixture.Part("tasks")[3]));
        var result = await client.GetAsync(task.Reference, TestContext.Current.CancellationToken); Assert.Equal("https://result.example.invalid/legacy.png", Assert.Single(result.Images).Url);
    }
}
