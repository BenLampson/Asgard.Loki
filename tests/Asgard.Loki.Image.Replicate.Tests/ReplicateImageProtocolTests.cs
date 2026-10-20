using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Replicate.Tests;

/// <summary>Replicate 原生输入、预测状态、原始产物与模型调用身份保真。</summary>
public sealed class ReplicateImageProtocolTests
{
    /// <summary>完整原生根和 input 字段逐项保持，不加入模型、默认参数或代理转换。</summary>
    [Fact]
    public async Task NativeWireAndCompletePredictionAreOriginal()
    {
        using var factory = ReplicateFixture.Factory(); factory.ConfigureClient = client =>
        { client.DefaultRequestHeaders.Add("Prefer", "unrelated"); client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated-key"); };
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://replicate-images.example.invalid/prefix/v1/models/fixture-owner/image-model/predictions", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-replicate-key", message.Headers.Authorization.Parameter);
            Assert.Equal("wait", Assert.Single(message.Headers.GetValues("Prefer"))); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("request"), ReplicateFixture.Element(await message.Content.ReadAsStringAsync(token)))); return ReplicateFixture.Json(ReplicateFixture.Part("success"));
        };
        var prediction = await new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(ReplicateFixture.Native(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("success"), prediction.Data)); Assert.True(prediction.IsSucceeded); Assert.True(prediction.IsTerminal);
        Assert.Equal("prediction-fixture", prediction.Id); Assert.Equal(new ImageResultMetadata("pictures", "fixture-owner/image-model", "replicate-test"), prediction.Metadata);
        Assert.Equal("https://result.example.invalid/a%2Fb.png?sig=A%2BB", Assert.Single(prediction.Images).Url); Assert.Equal(JsonValueKind.String, prediction.Images[0].Data.ValueKind);
        Assert.Equal("9007199254740993", prediction.Data.GetProperty("metrics").GetProperty("future").GetRawText()); Assert.False(prediction.Data.TryGetProperty("created", out _));
        Assert.True(JsonElement.DeepEquals(prediction.Data, prediction.AsCompletedResult().Data)); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>任何状态的完整原生输出、部分结果、错误和未知对象均保持，未知状态不能误判终态。</summary>
    [Theory]
    [InlineData("array", true, true, 2)] [InlineData("pending", false, false, 1)] [InlineData("failed", true, false, 1)]
    [InlineData("canceled", true, false, 0)] [InlineData("unknown", false, false, 0)] [InlineData("removed", true, true, 0)]
    public async Task NativeStateAndOutputAreNotFabricated(string name, bool terminal, bool succeeded, int count)
    {
        using var factory = ReplicateFixture.Factory(name); var prediction = await new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(ReplicateFixture.Native(), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part(name), prediction.Data)); Assert.Equal(terminal, prediction.IsTerminal); Assert.Equal(succeeded, prediction.IsSucceeded); Assert.Equal(count, prediction.Images.Count);
        if (!succeeded) { var error = Assert.Throws<ImageUpstreamException>(prediction.AsCompletedResult); Assert.Equal(200, error.StatusCode); Assert.True(JsonElement.DeepEquals(prediction.Data, ReplicateFixture.Element(error.ResponseBody))); }
        else Assert.Equal(count, prediction.AsCompletedResult().Images.Count);
        Assert.Equal(1, factory.Calls);
    }
    /// <summary>公共生成路由仅投递原生 input，不把 size/quality/n 改为其他原生设置。</summary>
    [Fact]
    public async Task PublicGenerationAndEditingUseOriginalInputAndTarget()
    {
        using var factory = ReplicateFixture.Factory(); factory.OnSend = async (message, token) =>
        {
            var body = ReplicateFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.Single(body.EnumerateObject());
            Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("request").GetProperty("input"), body.GetProperty("input"))); return ReplicateFixture.Json(ReplicateFixture.Part("array"));
        };
        var adapter = new ReplicateImageAdapter(factory, ReplicateFixture.Options()); await using var client = new LokiClientBuilder().AddImage(new()
        { Routes = [new() { Model = "pictures", Targets = [adapter] }], EditRoutes = [new() { Model = "pictures", Targets = [adapter] }] }).Build();
        var generation = ReplicateFixture.Request(); var result = await client.Image.GenerateAsync(generation, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Images.Count); result = await client.Image.EditAsync(new() { Model = generation.Model, Prompt = generation.Prompt, Parameters = generation.Parameters }, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Images.Count); Assert.Equal(2, factory.Calls);
    }
    /// <summary>原生 input 可以合法省略 prompt，类库不添加虚假提示词；快照在工厂前冻结。</summary>
    [Fact]
    public async Task NativeInputIsFrozenAndDoesNotRequireInventedPrompt()
    {
        var request = new ReplicateNativeImageRequest { PublicModel = "pictures", Parameters = new() { ["input"] = new JsonObject { ["image"] = "data:image/png;base64,AQID", ["future"] = false } } };
        using var factory = ReplicateFixture.Factory(); factory.OnCreate = request.Parameters.Clear; factory.OnSend = async (message, token) =>
        { var body = ReplicateFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.False(body.GetProperty("input").TryGetProperty("prompt", out _)); Assert.Equal("data:image/png;base64,AQID", body.GetProperty("input").GetProperty("image").GetString()); return ReplicateFixture.Json(ReplicateFixture.Part("success")); };
        await new ReplicateImageAdapter(factory, ReplicateFixture.Options()).PredictAsync(request, TestContext.Current.CancellationToken); Assert.Empty(request.Parameters); Assert.Equal(1, factory.Calls);
    }
    /// <summary>公共完整结果入口不把仍在处理的确认当成成功，也不追加轮询。</summary>
    [Fact]
    public async Task PublicGenerationCannotPublishPendingAsCompleted()
    {
        using var factory = ReplicateFixture.Factory("pending"); var error = await Assert.ThrowsAsync<ImageUpstreamException>(() => new ReplicateImageAdapter(factory, ReplicateFixture.Options()).GenerateAsync(ReplicateFixture.Request(), TestContext.Current.CancellationToken));
        Assert.True(JsonElement.DeepEquals(ReplicateFixture.Part("pending"), ReplicateFixture.Element(error.ResponseBody))); Assert.Equal(200, error.StatusCode); Assert.Equal(1, factory.Calls);
    }
}
