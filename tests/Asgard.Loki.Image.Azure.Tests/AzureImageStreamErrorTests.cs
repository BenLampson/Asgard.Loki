using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>原生错误保持原始 JSON、HTTP 和 SSE 身份，即使晚于原生完成项也不能先发布成功。</summary>
public sealed class AzureImageStreamErrorTests
{
    /// <summary>早期/晚期原生错误不制造完成项，保留原始 error event 和过滤细节。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task ErrorFrame_RetainsCompleteNativeIdentity(bool editing, bool late)
    {
        var errorFrame = AzureImageStreamFixture.Data().GetProperty("error_frame"); var raw = Encoding.UTF8.GetString(AzureImageFixture.Bytes("azure-image-error.sse"));
        var wire = (late ? AzureImageStreamFixture.Wire(editing) : "") + raw;
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        async Task Read() { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item); }
        if (editing)
        {
            var error = await Assert.ThrowsAsync<ImageEditUpstreamException>(Read); Assert.Equal(200, error.StatusCode); Assert.Equal("text/event-stream; charset=utf-8", error.ContentType);
            Assert.Equal(Encoding.UTF8.GetBytes(errorFrame.GetProperty("raw_data").GetString()!), error.Body.ToArray());
            var original = Assert.IsType<ImageEditEvent>(error.EditEvent); Assert.False(original.IsCompleted);
            Identity(original.Data, original.EventName, original.EventId, original.RetryMilliseconds, original.Metadata, errorFrame);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ImageUpstreamException>(Read); Assert.Equal(200, error.StatusCode); Assert.Equal("text/event-stream; charset=utf-8", error.ContentType);
            Assert.Equal(Encoding.UTF8.GetBytes(errorFrame.GetProperty("raw_data").GetString()!), error.Body.ToArray());
            var original = Assert.IsType<ImageGenerationEvent>(error.GenerationEvent);
            Identity(original.Data, original.EventName, original.EventId, original.RetryMilliseconds, original.Metadata, errorFrame);
        }
        AzureImageStreamLifecycleTests.NoCompleted(seen); Assert.False(image.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
    }

    /// <summary>原生 error 对象无需虚构 type；事件名本身为 error 也仍然失败。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task NativeErrorMarkers_AreNotTreatedAsUnknownSuccess(bool editing, bool byName)
    {
        var data = byName ? "{\"message\":\"native error\",\"Future\":9007199254740993}" : AzureImageFixture.Part("error").GetRawText();
        using var document = JsonDocument.Parse(data); var wire = AzureImageStreamFixture.Frame(document.RootElement, byName ? "error" : null);
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        async Task Read() { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item); }
        JsonElement actual;
        if (editing) { var error = await Assert.ThrowsAsync<ImageEditUpstreamException>(Read); actual = error.EditEvent!.Data; }
        else { var error = await Assert.ThrowsAsync<ImageUpstreamException>(Read); actual = error.GenerationEvent!.Data; }
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual)); Assert.False(actual.TryGetProperty("type", out _)); Assert.Empty(seen);
    }

    private static void Identity(JsonElement data, string? name, string? id, long? retry, ImageResultMetadata metadata, JsonElement expected)
    {
        Assert.Equal(expected.GetProperty("raw_data").GetString(), data.GetRawText()); Assert.Equal("error", name); Assert.Equal("original-error-id", id); Assert.Equal(7, retry);
        Assert.Equal(new ImageResultMetadata("images", "literal.image-deployment", "azure-test"), metadata);
        Assert.Equal("ResponsibleAIPolicyViolation", data.GetProperty("innererror").GetProperty("code").GetString());
        Assert.Equal("9007199254740993", data.GetProperty("Future").GetProperty("Big").GetRawText());
    }
}
