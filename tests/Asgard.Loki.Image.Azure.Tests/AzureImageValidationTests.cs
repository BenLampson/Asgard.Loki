using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>固定目标、参数和借用上传在 HTTP 前验证，不能改变另一操作的输入。</summary>
public sealed class AzureImageValidationTests
{
    /// <summary>部署不删点或猜测，拒绝歧义路径、认证头注入和无效本地策略。</summary>
    [Theory]
    [InlineData("relative")] [InlineData("query")] [InlineData("fragment")] [InlineData("userinfo")] [InlineData("escaped-path")]
    [InlineData("empty-deployment")] [InlineData("dot-deployment")] [InlineData("slash-deployment")] [InlineData("empty-key")]
    [InlineData("header-key")] [InlineData("nonascii-key")] [InlineData("empty-version")] [InlineData("header-version")]
    [InlineData("request-limit")] [InlineData("response-limit")] [InlineData("upload-limit")] [InlineData("empty-id")]
    [InlineData("empty-client")] [InlineData("empty-model")] [InlineData("unicode-model")]
    public void InvalidTarget_FailsBeforeCreatingClient(string kind)
    {
        using var factory = new AzureImageTestFactory();
        var endpoint = kind switch { "relative" => "/relative", "query" => "https://azure.invalid/?x=1", "fragment" => "https://azure.invalid/#part",
            "userinfo" => "https://secret@azure.invalid/", "escaped-path" => "https://azure.invalid/a%2fb/", _ => "https://azure.invalid/" };
        var deployment = kind switch { "empty-deployment" => "", "dot-deployment" => "..", "slash-deployment" => "a/b", _ => "original.model-deployment" };
        var key = kind switch { "empty-key" => "", "header-key" => "bad\r\nvalue", "nonascii-key" => "凭据", _ => "fixture-key" };
        var options = AzureImageFixture.Options(endpoint: endpoint, deployment: deployment, key: key,
            version: kind == "empty-version" ? "" : kind == "header-version" ? "x\nvalue" : "version",
            requestLimit: kind == "request-limit" ? 0 : 1000, responseLimit: kind == "response-limit" ? -1 : 1000,
            uploadLimit: kind == "upload-limit" ? 0 : 1000, id: kind == "empty-id" ? "" : "fixture",
            client: kind == "empty-client" ? "" : "fixture", bodyModel: kind == "empty-model" ? "" : kind == "unicode-model" ? "\ud800" : null);
        Assert.ThrowsAny<ArgumentException>(() => new AzureImageAdapter(factory, options));
        Assert.ThrowsAny<ArgumentException>(() => new AzureImageEditAdapter(factory, options)); Assert.Equal(0, factory.Creates);
    }

    /// <summary>公共字段不可覆盖，非法原生 JSON/Unicode 或过大请求不能发送。</summary>
    [Theory]
    [InlineData("model")] [InlineData("prompt")] [InlineData("model-alias")] [InlineData("prompt-alias")] [InlineData("undefined")]
    [InlineData("duplicate")] [InlineData("unicode")] [InlineData("stream-type")] [InlineData("stream-alias")] [InlineData("limit")]
    public async Task InvalidGeneration_FailsBeforeHttp(string kind)
    {
        using var factory = new AzureImageTestFactory(); var request = AzureImageFixture.Generation();
        if (kind is "model" or "prompt" or "model-alias" or "prompt-alias") request.Parameters[kind switch { "model-alias" => "MoDeL", "prompt-alias" => "Prompt", _ => kind }] = JsonSerializer.SerializeToElement("override");
        if (kind == "undefined") request.Parameters["future"] = default;
        if (kind == "duplicate") { using var document = JsonDocument.Parse("{\"x\":1,\"x\":2}"); request.Parameters["future"] = document.RootElement.Clone(); }
        if (kind == "unicode") request = request with { Prompt = "\ud800" };
        if (kind == "stream-type") request.Parameters["stream"] = JsonSerializer.SerializeToElement("false");
        if (kind == "stream-alias") request.Parameters["Stream"] = JsonSerializer.SerializeToElement(false);
        var adapter = new AzureImageAdapter(factory, AzureImageFixture.Options(requestLimit: kind == "limit" ? 1 : 10000));
        if (kind is "undefined" or "duplicate") await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GenerateAsync(request, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.GenerateAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Creates);
    }

    /// <summary>原生字段和上传角色不能被表单扩展覆盖或复用同一流。</summary>
    [Theory]
    [InlineData("model")] [InlineData("Prompt")] [InlineData("image[]")] [InlineData("images[0]")] [InlineData("mask")]
    [InlineData("header-name")] [InlineData("null-value")] [InlineData("duplicate-single")] [InlineData("stream-type")]
    [InlineData("same-source")] [InlineData("empty-images")] [InlineData("filename")] [InlineData("content-type")] [InlineData("limit")]
    public async Task InvalidMultipart_FailsBeforeReadingUpload(string kind)
    {
        using var upload = new AzureImageReadStream([1, 2, 255]); using var factory = new AzureImageTestFactory();
        var image = new ImageUpload { Content = upload, FileName = kind == "filename" ? "bad\nfile.png" : "file.png", ContentType = kind == "content-type" ? "bad-type" : "image/png" };
        var fields = kind switch
        {
            "header-name" => new[] { new ImageMultipartField("bad\nname", "value") },
            "null-value" => [new("future", null!)], "duplicate-single" => [new("n", "1"), new("n", "2")],
            "stream-type" => [new("stream", "False")], "same-source" or "empty-images" or "filename" or "content-type" or "limit" => [],
            _ => [new(kind, "override")]
        };
        var request = new ImageMultipartEditRequest { Model = "images", Prompt = "original", Images = kind == "empty-images" ? [] : [image],
            Mask = kind == "same-source" ? image : null, Fields = fields };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new AzureImageEditAdapter(factory, AzureImageFixture.Options(requestLimit: kind == "limit" ? 1 : 10000))
            .EditMultipartAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Creates); Assert.Equal(0, upload.Consumed); Assert.False(upload.Disposed);
    }

    /// <summary>未实现流式入口与非原生 JSON 引用不制造事件、下载引用或静默转成 multipart。</summary>
    [Fact]
    public async Task UnsupportedProtocols_FailLocallyWithoutFallback()
    {
        using var upload = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory(); var token = TestContext.Current.CancellationToken;
        var generation = new AzureImageAdapter(factory, AzureImageFixture.Options()); var edit = new AzureImageEditAdapter(factory, AzureImageFixture.Options());
        Assert.Throws<NotSupportedException>(() => edit.StreamEditAsync(new() { Model = "images", Prompt = "original" }, token));
        await Assert.ThrowsAsync<NotSupportedException>(() => edit.EditAsync(new() { Model = "images", Prompt = "original" }, token));
        var request = AzureImageFixture.Generation(); request.Parameters["stream"] = JsonSerializer.SerializeToElement(true);
        await Assert.ThrowsAsync<NotSupportedException>(() => generation.GenerateAsync(request, token));
        var multipart = new ImageMultipartEditRequest { Model = "images", Prompt = "original", Images = [new() { Content = upload, FileName = "file.png" }], Fields = [new("stream", "true")] };
        await Assert.ThrowsAsync<NotSupportedException>(() => edit.EditMultipartAsync(multipart, token));
        Assert.Equal(0, factory.Creates); Assert.Equal(0, upload.Consumed);
    }

    /// <summary>合法 JSON 仍须有真实图片数组与原生字符串载荷，不能把策略消息当图片。</summary>
    [Theory]
    [InlineData("[]")] [InlineData("{\"data\":[]}")] [InlineData("{\"data\":[{}]}")] [InlineData("{\"data\":[{\"b64_json\":7}]}")]
    [InlineData("{\"data\":[{\"url\":\"\"}]}")] [InlineData("{\"data\":[{\"url\":\"fixture\"}],\"future\":{\"a\":1,\"a\":2}}")]
    [InlineData("{\"data\":[{\"url\":\"fixture\"}],\"future\":\"\\ud800\"}")]
    public async Task InvalidSuccessJson_DoesNotBecomeImageResult(string raw)
    {
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageFixture.Json(raw)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new AzureImageAdapter(factory, AzureImageFixture.Options()).GenerateAsync(AzureImageFixture.Generation(), TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>配置 JSON 不序列化密钥；公开目标信息仍保留部署和版本。</summary>
    [Fact]
    public void OptionSerialization_OmitsCredential()
    {
        var json = JsonSerializer.Serialize(AzureImageFixture.Options()); Assert.DoesNotContain("fixture-azure-image-key", json);
        Assert.Contains("literal.image-deployment", json); Assert.Contains("fixture-version", json);
    }
}
