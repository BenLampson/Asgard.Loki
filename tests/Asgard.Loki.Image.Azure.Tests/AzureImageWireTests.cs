using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>独立完整字节/JSON 样本确保固定 Azure 目标及原生字段不丢失。</summary>
public sealed class AzureImageWireTests
{
    /// <summary>日期版本的原生路径、ApiKey 和可选正文模型分别绑定，原文/未知数值不经过中间浮点表示。</summary>
    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task Generation_MatchesIndependentWireAndFullNativeResult(int index)
    {
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            Target(message, "generations"); Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            var bytes = await message.Content.ReadAsByteArrayAsync(token); using var document = JsonDocument.Parse(bytes);
            Assert.True(JsonElement.DeepEquals(AzureImageFixture.Part("generation_requests")[index], document.RootElement));
            Assert.Contains("9007199254740993", Encoding.UTF8.GetString(bytes)); Assert.DoesNotContain("\"model\":\"images\"", Encoding.UTF8.GetString(bytes));
            return AzureImageFixture.Json(AzureImageFixture.Part("result"));
        } };
        await using var client = AzureImageFixture.Build(factory, options: AzureImageFixture.Options(bodyModel: index == 1 ? "native-body-model" : null));
        var result = await client.Image.GenerateAsync(AzureImageFixture.Generation(index), TestContext.Current.CancellationToken);
        Result(result); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.Equal("azure-image-test", factory.Name); Assert.False(factory.Disposed);
    }

    /// <summary>单图 image 与多图 image[]/独立 mask 匹配完整金样；只有随机边界被替换。</summary>
    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task MultipartEditing_MatchesIndependentCompleteBinaryWire(int index)
    {
        var form = AzureImageFixture.Part("multipart_edits")[index]; var imageData = form.GetProperty("images");
        var images = imageData.EnumerateArray().Select(item => new AzureImageReadStream(Convert.FromHexString(item.GetProperty("hex").GetString()!))).ToArray();
        using var mask = index == 1 ? new AzureImageReadStream(Convert.FromHexString(form.GetProperty("mask").GetProperty("hex").GetString()!)) : null;
        try
        {
            using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
            {
                Target(message, "edits"); Assert.Null(message.Content!.Headers.ContentLength);
                var bytes = await message.Content.ReadAsByteArrayAsync(token);
                var boundary = message.Content.Headers.ContentType!.Parameters.Single(item => item.Name == "boundary").Value!.Trim('"');
                // Latin1 是一一字节映射，仅替换随机 ASCII 边界，不解码图像或有损转换 UTF-8。
                var normalized = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace(boundary, "<BOUNDARY>", StringComparison.Ordinal));
                Assert.Equal(AzureImageFixture.Bytes(form.GetProperty("golden").GetString()!), normalized);
                return AzureImageFixture.Json(AzureImageFixture.Part("result"));
            } };
            var request = AzureImageFixture.Edit(index, images, mask);
            await using var client = AzureImageFixture.Build(factory, true, AzureImageFixture.Options(bodyModel: index == 1 ? "native-body-model" : null));
            Result(await client.Image.EditMultipartAsync(request, TestContext.Current.CancellationToken));
            Assert.All(images, image => { Assert.True(image.Eof); Assert.False(image.Disposed); });
            if (mask is not null) { Assert.True(mask.Eof); Assert.False(mask.Disposed); }
            Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.False(factory.Disposed);
        }
        finally { foreach (var image in images) image.Dispose(); }
    }

    /// <summary>原始结果不依赖响应流的文档寿命，不计算用量或规范化过滤结果。</summary>
    private static void Result(ImageGenerationResult result)
    {
        Assert.Equal(AzureImageFixture.Part("result").GetRawText(), result.Data.GetRawText());
        Assert.Equal(new ImageResultMetadata("images", "literal.image-deployment", "azure-test"), result.Metadata); Assert.Equal(2, result.Images.Count);
        Assert.Equal("AAEC/w==", result.Images[0].Base64);
        Assert.Equal("https://media.example.invalid/image%2Ftwo.png?sig=fixture%2B2&part=0", result.Images[1].Url);
        Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("input_tokens").GetRawText());
        Assert.False(result.Images[0].Data.GetProperty("content_filter_results").GetProperty("hate").GetProperty("filtered").GetBoolean());
    }
    /// <summary>大小写不敏感认证头与转义后的单独版本参数，不带入工厂默认认证。</summary>
    private static void Target(HttpRequestMessage message, string operation)
    {
        Assert.Equal(HttpMethod.Post, message.Method);
        Assert.Equal("https://azure-image.example.invalid/prefix/openai/deployments/literal.image-deployment/images/" + operation + "?api-version=fixture-version%2Bliteral", message.RequestUri!.AbsoluteUri);
        Assert.Equal(new[] { "fixture-azure-image-key" }, message.Headers.GetValues("api-key")); Assert.Null(message.Headers.Authorization);
    }

    /// <summary>原生字段在工厂回调前冻结，调用者修改集合不改变已准备正文。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Requests_AreFrozenBeforeCreatingClient(bool editing)
    {
        using var image = new AzureImageReadStream([1, 2, 255]); var generation = AzureImageFixture.Generation();
        var fields = new List<ImageMultipartField> { new("Future[]", "one"), new("Future[]", "two") };
        var uploads = new List<ImageUpload> { new() { Content = image, FileName = "original.png", ContentType = "image/png" } };
        var edit = new ImageMultipartEditRequest { Model = "images", Prompt = "original", Images = uploads, Fields = fields };
        using var factory = new AzureImageTestFactory { OnCreate = () => { generation.Parameters.Clear(); fields.Clear(); uploads.Clear(); }, OnSend = async (message, token) =>
        {
            var body = Encoding.UTF8.GetString(await message.Content!.ReadAsByteArrayAsync(token));
            if (editing) { Assert.Contains("original.png", body); Assert.Contains("one", body); Assert.Contains("two", body); }
            else Assert.Contains("9007199254740993", body);
            return AzureImageFixture.Json(AzureImageFixture.Part("result"));
        } };
        if (editing) await new AzureImageEditAdapter(factory, AzureImageFixture.Options()).EditMultipartAsync(edit, TestContext.Current.CancellationToken);
        else await new AzureImageAdapter(factory, AzureImageFixture.Options()).GenerateAsync(generation, TestContext.Current.CancellationToken);
        Assert.False(image.Disposed); Assert.Equal(1, factory.Calls);
    }
}
