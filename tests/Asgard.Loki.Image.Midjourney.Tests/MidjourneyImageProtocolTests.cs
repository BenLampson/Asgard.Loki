using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Midjourney.Tests;

/// <summary>完整原生图片操作、父任务/按钮/索引和未知字段保持，确认不等于生成完成。</summary>
public sealed class MidjourneyImageProtocolTests
{
    /// <summary>每种保留的原生操作逐一验证实际路径、完整请求和原始确认。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task EveryNativeSubmitOperationPreservesWholeWire(int index)
    {
        var vector = MidjourneyFixture.Part("submissions")[index]; using var factory = MidjourneyFixture.Factory(); factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("http://mj-images.example.invalid:8081/prefix/mj-fast/mj/" + vector.GetProperty("path").GetString(), message.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-mj-secret", Assert.Single(message.Headers.GetValues("mj-api-secret"))); Assert.Null(message.Headers.Authorization);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.True(JsonElement.DeepEquals(vector.GetProperty("body"), MidjourneyFixture.Element(await message.Content.ReadAsStringAsync(token))));
            return MidjourneyFixture.Json(MidjourneyFixture.Part("submitted"));
        };
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options()); var result = await client.SubmitAsync(Enum.Parse<MidjourneyImageOperation>(vector.GetProperty("operation").GetString()!), MidjourneyFixture.Body(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part("submitted"), result.Data)); Assert.Equal("task-fixture", result.Reference.TaskId); Assert.Equal(client.ReferenceTask("task-fixture"), result.Reference);
        Assert.Equal(1, result.Code); Assert.Null(result.Status); Assert.False(result.IsTerminal); Assert.Equal("midjourney-images-test", factory.ClientName);
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed); Assert.Equal(200, result.HttpResponse.StatusCode);
        Assert.True(JsonElement.DeepEquals(result.Data, MidjourneyFixture.Element(System.Text.Encoding.UTF8.GetString(result.HttpResponse.Body.Span))));
    }
    /// <summary>已有和排队确认保留 code/status，不把任务 ID 或进度当成图片。</summary>
    [Theory]
    [InlineData("existing", 21, "SUCCESS", true)] [InlineData("queued", 22, null, false)]
    public async Task ExistingAndQueuedConfirmationsRemainDistinct(string name, long code, string? status, bool terminal)
    {
        using var factory = MidjourneyFixture.Factory(name); var result = await new MidjourneyImageClient(factory, MidjourneyFixture.Options()).SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part(name), result.Data)); Assert.Equal(code, result.Code); Assert.Equal(status, result.Status); Assert.Equal(terminal, result.IsTerminal); Assert.Equal(1, factory.Calls);
    }
    /// <summary>未知 JSON 在第一次工厂之前冻结，后续清空参数不能改变请求。</summary>
    [Fact]
    public async Task FullRequestSnapshotPrecedesFactory()
    {
        var body = MidjourneyFixture.Body(5); using var factory = MidjourneyFixture.Factory(); factory.OnCreate = body.Clear; factory.OnSend = async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part("submissions")[5].GetProperty("body"), MidjourneyFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return MidjourneyFixture.Json(MidjourneyFixture.Part("submitted")); };
        await new MidjourneyImageClient(factory, MidjourneyFixture.Options()).SubmitAsync(MidjourneyImageOperation.Action, body, TestContext.Current.CancellationToken); Assert.Empty(body);
    }
    /// <summary>显式匿名与独立原始密钥不借用工厂 Bearer、Cookie 或其他能力凭据。</summary>
    [Theory]
    [InlineData(null)] [InlineData("fixture-mj-secret")]
    public async Task AuthenticationModeIsExplicitAndIsolated(string? secret)
    {
        using var factory = MidjourneyFixture.Factory(); factory.ConfigureClient = client =>
        { client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated-key"); client.DefaultRequestHeaders.Add("mj-api-secret", "unrelated-key"); client.DefaultRequestHeaders.Add("Cookie", "unrelated-cookie"); };
        factory.OnSend = (message, _) =>
        {
            Assert.Null(message.Headers.Authorization); Assert.False(message.Headers.Contains("Cookie")); Assert.Equal(secret is not null, message.Headers.Contains("mj-api-secret"));
            if (secret is not null) Assert.Equal(secret, Assert.Single(message.Headers.GetValues("mj-api-secret")));
            return Task.FromResult(MidjourneyFixture.Json(MidjourneyFixture.Part("submitted")));
        };
        var options = MidjourneyFixture.Options(secret: secret); Assert.DoesNotContain("fixture-mj-secret", JsonSerializer.Serialize(options));
        await new MidjourneyImageClient(factory, options).SubmitAsync(MidjourneyImageOperation.Imagine, MidjourneyFixture.Body(), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>输入上传是一个 JSON 交换，原生 URL 数组顺序/元数据不转为普通任务或下载。</summary>
    [Fact]
    public async Task UploadKeepsOriginalUrlArrayAndDoesNotStartTask()
    {
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = async (message, token) =>
        {
            Assert.EndsWith("/submit/upload-discord-images", message.RequestUri!.AbsoluteUri);
            Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part("upload_request").GetProperty("body"), MidjourneyFixture.Element(await message.Content!.ReadAsStringAsync(token))));
            return MidjourneyFixture.Json(MidjourneyFixture.Part("upload_result"));
        };
        var result = await new MidjourneyImageClient(factory, MidjourneyFixture.Options()).UploadInputsAsync(JsonNode.Parse(MidjourneyFixture.Part("upload_request").GetProperty("body").GetRawText())!.AsObject(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part("upload_result"), result.Data)); Assert.Equal(2, result.Data.GetProperty("result").GetArrayLength()); Assert.Equal(1, factory.Calls);
    }
    /// <summary>seed 值与 seed 排队确认保留原始语义，既不自动查询也不绑定返回值为图片任务。</summary>
    [Theory]
    [InlineData("seed_result")] [InlineData("seed_queued")]
    public async Task SeedRequestDoesNotInventCompletedSeedOrTask(string name)
    {
        using var factory = MidjourneyFixture.Factory(); factory.OnSend = (message, _) =>
        { Assert.Equal(HttpMethod.Get, message.Method); Assert.EndsWith("/task/task-fixture/image-seed", message.RequestUri!.AbsoluteUri); Assert.Null(message.Content); return Task.FromResult(MidjourneyFixture.Json(MidjourneyFixture.Part(name))); };
        var client = new MidjourneyImageClient(factory, MidjourneyFixture.Options()); var result = await client.RequestImageSeedAsync(client.ReferenceTask("task-fixture"), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(MidjourneyFixture.Part(name), result.Data)); Assert.Equal(1, factory.Calls);
    }
}
