using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Tests;

/// <summary>原生 JSON/表单保真、输入/遮罩角色和独立完整目标的公共调用回归。</summary>
public sealed class ImageEditClientTests
{
    /// <summary>供应商原生引用、遮罩及未知 JSON 在派发前独立复制，源文档可释放。</summary>
    [Fact]
    public async Task JsonSnapshot_PreservesNativeReferencesAndUnknownFields()
    {
        var request = ImageEditFixture.Json(); using var document = JsonDocument.Parse(request.Parameters["future"].GetRawText());
        request.Parameters["future"] = document.RootElement; var expected = ImageEditFixture.Golden("edit-native-request");
        var adapter = new StubImageEditAdapter { OnJson = _ => { document.Dispose(); request.Parameters.Clear(); } };
        await using var client = ImageEditFixture.Build(adapter);
        var result = await client.Image.EditAsync(request, TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement(adapter.LastJson)));
        Assert.True(JsonElement.DeepEquals(ImageEditFixture.Golden("edit-native-response"), result.Data));
        Assert.Equal(new ImageResultMetadata("pictures", "native-image-model", adapter.Id), result.Metadata);
        Assert.Equal(2, result.Images.Count); Assert.Equal(new byte[] { 1, 2, 3 }, result.Images[0].Decode().ToArray());
        Assert.Equal("https://media.invalid/result.png", result.Images[1].Url); Assert.Equal(1, adapter.Edits);
    }

    /// <summary>图片顺序、独立遮罩、重复字段和原始值保留，源列表在派发时可修改。</summary>
    [Fact]
    public async Task MultipartSnapshot_PreservesImageMaskRolesAndBorrowedStreams()
    {
        using var one = new ObservedImageStream([0, 255]); using var two = new ObservedImageStream([1, 254]); using var mask = new ObservedImageStream([2, 253]);
        var request = ImageEditFixture.Multipart(one, two, mask); var images = request.Images.ToList(); var fields = request.Fields.ToList();
        request = request with { Images = images, Fields = fields }; var expectedFields = fields.ToArray();
        var adapter = new StubImageEditAdapter { OnMultipart = _ => { images.Clear(); fields.Clear(); } }; var client = ImageEditFixture.Build(adapter);
        var result = await client.Image.EditMultipartAsync(request, TestContext.Current.CancellationToken); var snapshot = adapter.LastMultipart!;
        Assert.Equal(new[] { "一.png", "two.webp" }, snapshot.Images.Select(image => image.FileName)); Assert.Equal("mask.png", snapshot.Mask!.FileName);
        Assert.Same(one, snapshot.Images[0].Content); Assert.Same(two, snapshot.Images[1].Content); Assert.Same(mask, snapshot.Mask.Content);
        Assert.NotSame(request.Mask, snapshot.Mask); Assert.Equal(expectedFields, snapshot.Fields);
        Assert.Null(snapshot.Images[1].ContentType); Assert.Equal("image/png", snapshot.Images[0].ContentType);
        Assert.DoesNotContain("\"Content\":", JsonSerializer.Serialize(snapshot)); Assert.True(JsonElement.DeepEquals(ImageEditFixture.Golden("edit-native-response"), result.Data));
        await client.DisposeAsync(); Assert.False(adapter.Disposed);
        Assert.All(new[] { one, two, mask }, upload => { Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed); });
    }

    /// <summary>核心不回卷输入或遮罩，当前位置由调用者持有；可选遮罩可省略。</summary>
    [Fact]
    public async Task MultipartPositionAndOmittedMask_AreNative()
    {
        using var one = new MemoryStream([0, 1, 2]); using var two = new MemoryStream([3, 4, 5]); one.Position = 1; two.Position = 2;
        var adapter = new StubImageEditAdapter { OnMultipart = snapshot =>
        { Assert.Equal(1, snapshot.Images[0].Content.Position); Assert.Equal(2, snapshot.Images[1].Content.Position); Assert.Null(snapshot.Mask); } };
        await using var client = ImageEditFixture.Build(adapter);
        await client.Image.EditMultipartAsync(new() { Model = "pictures", Prompt = "native", Images = [new() { Content = one, FileName = "one" }, new() { Content = two, FileName = "two" }] }, TestContext.Current.CancellationToken);
        Assert.Equal(1, one.Position); Assert.Equal(2, two.Position); Assert.True(one.CanRead); Assert.True(two.CanRead);
    }

    /// <summary>JSON/multipart 失败都不自动切换目标、转换输入协议或重新上传。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Failure_DoesNotRetryOrFallBack(bool multipart)
    {
        using var one = new ObservedImageStream([1]); using var two = new ObservedImageStream([2]); using var mask = new ObservedImageStream([3]);
        var first = new StubImageEditAdapter("first") { Failure = new IOException("fixture failure") }; var second = new StubImageEditAdapter("second");
        await using var client = ImageEditFixture.Build(first, second);
        if (multipart) await Assert.ThrowsAsync<IOException>(() => client.Image.EditMultipartAsync(ImageEditFixture.Multipart(one, two, mask), TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<IOException>(() => client.Image.EditAsync(ImageEditFixture.Json(), TestContext.Current.CancellationToken));
        Assert.Equal(1, first.Edits + first.Multiparts); Assert.Equal(0, second.Edits + second.Multiparts);
        Assert.All(new[] { one, two, mask }, upload => { Assert.Equal(0, upload.Reads); Assert.False(upload.Disposed); });
    }

    /// <summary>注册时复制路由/目标，多个客户端和不同编辑模型各自保持选择状态。</summary>
    [Fact]
    public async Task RoutingState_IsIndependentAndRegistrationIsSnapshot()
    {
        var first = new StubImageEditAdapter("first"); var second = new StubImageEditAdapter("second");
        var targets = new List<IImageEditAdapter> { first, second }; var routes = new List<ImageEditRoute>
        { new() { Model = "pictures", Targets = targets }, new() { Model = "other", Targets = targets } };
        var builder = new LokiClientBuilder().AddImage(new() { EditRoutes = routes }); targets.Clear(); routes.Clear();
        await using var one = builder.Build(); await using var two = builder.Build(); var token = TestContext.Current.CancellationToken; var request = ImageEditFixture.Json();
        Assert.Equal("first", (await one.Image.EditAsync(request, token)).Metadata.ProviderId);
        Assert.Equal("first", (await two.Image.EditAsync(request, token)).Metadata.ProviderId);
        Assert.Equal("first", (await one.Image.EditAsync(request with { Model = "other" }, token)).Metadata.ProviderId);
        Assert.Equal("second", (await one.Image.EditAsync(request, token)).Metadata.ProviderId);
    }

    /// <summary>生成与编辑同名路由彼此独立，新构建的客户端不共享生成计数或编辑目标。</summary>
    [Fact]
    public async Task GenerationAndEditing_AreSeparateCompleteTargetSets()
    {
        var generationOne = new StubImageEditAdapter("generate-one"); var generationTwo = new StubImageEditAdapter("generate-two"); var edit = new StubImageEditAdapter("edit");
        var builder = new LokiClientBuilder().AddImage(new()
        { Routes = [new() { Model = "pictures", Targets = [generationOne, generationTwo] }], EditRoutes = [new() { Model = "pictures", Targets = [edit] }] });
        await using var first = builder.Build(); await using var second = builder.Build(); var token = TestContext.Current.CancellationToken;
        var generate = new ImageGenerationRequest { Model = "pictures", Prompt = "native" };
        Assert.Equal("generate-one", (await first.Image.GenerateAsync(generate, token)).Metadata.ProviderId);
        Assert.Equal("generate-one", (await second.Image.GenerateAsync(generate, token)).Metadata.ProviderId);
        Assert.Equal("edit", (await first.Image.EditAsync(ImageEditFixture.Json(), token)).Metadata.ProviderId);
        Assert.Equal("generate-two", (await first.Image.GenerateAsync(generate, token)).Metadata.ProviderId);
        Assert.Equal(1, edit.Edits); Assert.Equal(0, generationOne.Edits + generationTwo.Edits);
    }

    /// <summary>仅编辑配置不需要文本生成目标；仅生成配置不能隐式执行编辑。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OperationRegistration_DoesNotBorrowOtherRoutes(bool edits)
    {
        var adapter = new StubImageEditAdapter();
        await using var client = new LokiClientBuilder().AddImage(edits
            ? new ImageOptions { EditRoutes = [new() { Model = "pictures", Targets = [adapter] }] }
            : new ImageOptions { Routes = [new() { Model = "pictures", Targets = [adapter] }] }).Build();
        if (edits) await Assert.ThrowsAsync<InvalidOperationException>(() => client.Image.GenerateAsync(new() { Model = "pictures", Prompt = "native" }, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.Image.EditAsync(ImageEditFixture.Json(), TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.Generations + adapter.Edits);
    }
}
