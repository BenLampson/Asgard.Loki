using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Tests;

/// <summary>原生图像便利视图保持原始 JSON 和独立集合所有权。</summary>
public sealed class NativeImageViewTests
{
    /// <summary>供应商图像视图不能改写完整结果，调用者更改集合不影响快照。</summary>
    [Fact]
    public void NativeViewKeepsJsonAndSnapshotsCollection()
    {
        using var document = JsonDocument.Parse("{\"output\":{\"image\":\"https://example.invalid/a%2Fb\",\"future\":false}}");
        var original = document.RootElement; var item = new GeneratedImage(original.GetProperty("output"), "https://example.invalid/a%2Fb", null);
        var list = new List<GeneratedImage> { item }; var result = new ImageGenerationResult(original, new("public", "actual", "vendor"), list); list.Clear();
        Assert.Same(item, Assert.Single(result.Images)); Assert.Equal("https://example.invalid/a%2Fb", item.Url); Assert.Null(item.Base64);
        Assert.True(JsonElement.DeepEquals(original, result.Data)); Assert.False(item.Data.TryGetProperty("url", out _));
    }
    /// <summary>显式空视图不能退回猜测的 OpenAI data 数组。</summary>
    [Fact]
    public void ExplicitEmptyViewOverridesLegacyArray()
    {
        using var document = JsonDocument.Parse("{\"data\":[{\"url\":\"legacy\"}]}");
        Assert.Empty(new ImageGenerationResult(document.RootElement, new("p", "m", "v"), []).Images);
        Assert.Equal("legacy", Assert.Single(new ImageGenerationResult(document.RootElement, new("p", "m", "v")).Images).Url);
    }
    /// <summary>原生 Base64 仅在明确 Decode 时解码，原始字段名保留。</summary>
    [Fact]
    public void NativeBase64IsExplicitAndJsonStaysOriginal()
    {
        using var document = JsonDocument.Parse("{\"bytes\":\"AQID\"}"); var image = new GeneratedImage(document.RootElement, null, "AQID");
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Decode().ToArray()); Assert.Null(image.Url); Assert.False(image.Data.TryGetProperty("b64_json", out _));
    }
}
