using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Midjourney.Tests;

/// <summary>任务身份、原生终态、持久化完整目标绑定及批量 ID 关联。</summary>
public sealed class MidjourneyImageTaskTests
{
    /// <summary>原生任务正文完整保留，文本类结果无需伪造图片；100% 不完成未知或弹窗状态。</summary>
    [Theory]
    [InlineData("task_success", true)] [InlineData("task_running", false)] [InlineData("task_modal", false)]
    [InlineData("task_future", false)] [InlineData("task_failed", true)] [InlineData("task_canceled", true)]
    [InlineData("describe_success", true)] [InlineData("shorten_success", true)]
    public async Task OriginalTaskStateAndEveryOutcomeRemainWhole(string name, bool terminal)
    {
        using var factory = MidjourneyFixture.Factory(name); var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options());
        var task = await client.GetAsync(client.ReferenceTask("task-fixture"), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part(name), task.Data)); Assert.Equal(terminal, task.IsTerminal); Assert.Equal("task-fixture", task.Reference.TaskId);
        Assert.Equal("9007199254740993", task.Data.GetProperty("submitTime").GetRawText()); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
        if (name == "shorten_success") { Assert.Null(task.ImageUrl); Assert.False(task.Data.TryGetProperty("imageUrl", out _)); }
    }
    /// <summary>重建实例/引用仍能查询同一完整目标，不依赖内存任务表。</summary>
    [Fact]
    public async Task SerializedTaskReferenceWorksAfterClientReconstruction()
    {
        using var factory = MidjourneyFixture.Factory("task_success"); var first = new MidjourneyImageClient(factory, MidjourneyFixture.Options());
        var reference = JsonSerializer.Deserialize<MidjourneyImageTaskReference>(JsonSerializer.Serialize(first.ReferenceTask("task-fixture")))!;
        var task = await new MidjourneyImageClient(factory, MidjourneyFixture.Options()).GetAsync(reference, TestContext.Current.CancellationToken); Assert.Equal(reference, task.Reference); Assert.Equal(1, factory.Calls);
    }
    /// <summary>任务 ID 保持原始不透明内容，发送时仅作为一个路径段转义一次。</summary>
    [Fact]
    public async Task OpaqueTaskIdIsEscapedWithoutChangingIdentityOrRoot()
    {
        const string id = "id/with?query#fragment%2F"; var data = JsonNode.Parse(MidjourneyFixture.Part("task_success").GetRawText())!.AsObject(); data["id"] = id;
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = (message, _) =>
        { Assert.Equal("http://mj-images.example.invalid:8081/prefix/mj-fast/mj/task/id%2Fwith%3Fquery%23fragment%252F/fetch", message.RequestUri!.AbsoluteUri); return Task.FromResult(MidjourneyFixture.Json(MidjourneyFixture.Element(data.ToJsonString()))); };
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options()); var task = await client.GetAsync(client.ReferenceTask(id), TestContext.Current.CancellationToken);
        Assert.Equal(id, task.Reference.TaskId); Assert.Equal(id, task.Data.GetProperty("id").GetString()); Assert.Equal(1, factory.Calls);
    }
    /// <summary>代理、凭据、客户端或限制变化必须在单次/seed/批量查询创建 HTTP 之前失败。</summary>
    [Theory]
    [InlineData("secret")] [InlineData("root")] [InlineData("id")] [InlineData("client")] [InlineData("limit")]
    public void FullTargetDriftFailsBeforeAnyQuery(string kind)
    {
        using var factory = MidjourneyFixture.Factory(); var original = new MidjourneyImageClient(factory, MidjourneyFixture.Options()).ReferenceTask("task-fixture");
        var options = MidjourneyFixture.Options(secret: kind == "secret" ? null : "fixture-mj-secret", root: kind == "root" ? "https://other.example.invalid/mj/" : "http://mj-images.example.invalid:8081/prefix/mj-fast/mj/", id: kind == "id" ? "different" : "midjourney-test", client: kind == "client" ? "different" : "midjourney-images-test", responseLimit: kind == "limit" ? 7 : 16 * 1024 * 1024);
        var client = new MidjourneyImageClient(factory, options);
        Assert.Throws<ArgumentException>(() => { _ = client.GetAsync(original, TestContext.Current.CancellationToken); });
        Assert.Throws<ArgumentException>(() => { _ = client.RequestImageSeedAsync(original, TestContext.Current.CancellationToken); });
        Assert.Throws<ArgumentException>(() => { _ = client.GetManyAsync([original], cancellationToken: TestContext.Current.CancellationToken); }); Assert.Equal(0, factory.Creates);
    }
    /// <summary>批量返回重新排序时按原生 ID 关联，不把另一个任务的结果交给第一个引用。</summary>
    [Fact]
    public async Task ReorderedTaskListAssociatesByNativeIdAndKeepsNativeOrder()
    {
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = async (message, token) =>
        {
            Assert.EndsWith("/task/list-by-condition", message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part("list_request").GetProperty("body"), MidjourneyFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return MidjourneyFixture.Json(MidjourneyFixture.Part("list_result"));
        };
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options()); var first = client.ReferenceTask("task-fixture"); var second = client.ReferenceTask("second-fixture");
        var result = await client.GetManyAsync([first, second], new() { ["future"] = false }, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part("list_result"), result.Data)); Assert.Equal(second, result.Tasks[0].Reference); Assert.Equal(first, result.Tasks[1].Reference);
        Assert.Equal("IN_PROGRESS", result.Tasks[0].Status); Assert.Equal("SUCCESS", result.Tasks[1].Status); Assert.Equal(1, factory.Calls);
    }
    /// <summary>返回缺失任务保持缺失，不凭请求数补齐；空列表原样保持。</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingAndEmptyBatchResultsAreNotFabricated(bool empty)
    {
        var data = JsonNode.Parse(MidjourneyFixture.Part("list_result").GetRawText())!.AsArray(); data.RemoveAt(1); if (empty) data.Clear();
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(MidjourneyFixture.Json(MidjourneyFixture.Element(data.ToJsonString())));
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options()); var result = await client.GetManyAsync([client.ReferenceTask("task-fixture"), client.ReferenceTask("second-fixture")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(empty ? 0 : 1, result.Tasks.Count); Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Element(data.ToJsonString()), result.Data)); if (!empty) Assert.Equal("second-fixture", result.Tasks[0].Reference.TaskId);
    }
    /// <summary>其他任务、重复返回 ID 或矛盾成功不能悄悄替换原任务状态。</summary>
    [Theory]
    [InlineData("single-id")] [InlineData("batch-id")] [InlineData("batch-duplicate")] [InlineData("success-error")] [InlineData("video")]
    public async Task InvalidTaskAssociationOrOutcomeFailsExplicitly(string kind)
    {
        var data = JsonNode.Parse(MidjourneyFixture.Part("task_success").GetRawText())!.AsObject();
        if (kind is "single-id" or "batch-id") data["id"] = "unrelated-task";
        if (kind == "success-error") data["failReason"] = "native failure";
        if (kind == "video") data["action"] = "VIDEO";
        if (kind == "batch-duplicate") data["id"] = "task-fixture";
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(MidjourneyFixture.Json(MidjourneyFixture.Element(kind.StartsWith("batch", StringComparison.Ordinal) ? new JsonArray(data.DeepClone(), data.DeepClone()).ToJsonString() : data.ToJsonString())));
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options()); var reference = client.ReferenceTask("task-fixture");
        if (kind.StartsWith("batch", StringComparison.Ordinal)) await Assert.ThrowsAsync<InvalidDataException>(() => client.GetManyAsync([reference], cancellationToken: TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync(reference, TestContext.Current.CancellationToken)); Assert.Equal(1, factory.Calls);
    }
}
