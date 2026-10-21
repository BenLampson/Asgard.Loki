using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>显式请求/枚举所有权和标准 SSE 身份，不降级或重放请求。</summary>
public sealed class AzureImageStreamUsageTests
{
    /// <summary>参数在调用时固定，开始枚举前不创建客户端/读取图片。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequestSnapshot_PrecedesEnumerationAndFactoryCallbacks(bool editing)
    {
        using var image = new AzureImageReadStream([1]); var generation = AzureImageStreamFixture.Generation();
        var fields = AzureImageStreamFixture.Edit(1, [image]).Fields.ToList(); var images = new List<ImageUpload> { new() { Content = image, FileName = "file.png" } };
        var edit = new ImageMultipartEditRequest { Model = "images", Prompt = "original", Images = images, Fields = fields };
        using var document = JsonDocument.Parse("{\"big\":9007199254740993,\"enabled\":false,\"zero\":0,\"missing\":null}"); generation.Parameters["future-snapshot"] = document.RootElement;
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            var raw = Encoding.UTF8.GetString(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Contains("9007199254740993", raw);
            if (editing) { Assert.Contains("file.png", raw); Assert.Contains("name=stream\r\n\r\ntrue", raw); }
            else Assert.Contains("future-snapshot", raw);
            return AzureImageStreamFixture.Response(AzureImageStreamFixture.Wire(editing));
        } };
        var source = editing ? AzureImageStreamFixture.Observe(new AzureImageEditAdapter(factory, AzureImageFixture.Options()).StreamMultipartEditAsync(edit, TestContext.Current.CancellationToken))
            : AzureImageStreamFixture.Observe(new AzureImageAdapter(factory, AzureImageFixture.Options()).StreamAsync(generation, TestContext.Current.CancellationToken));
        document.Dispose(); generation.Parameters.Clear(); fields.Clear(); images.Clear(); Assert.Equal(0, factory.Creates); Assert.Equal(0, image.Consumed);
        await foreach (var _ in source) { }
        Assert.Equal(1, factory.Calls); Assert.False(image.Disposed);
    }

    /// <summary>同一流实例不能正常完成/早停后再次发送原始请求。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task RepeatedEnumeration_DoesNotRetryOrReupload(bool editing, bool earlyStop)
    {
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(AzureImageStreamFixture.Wire(editing))) };
        await using var client = AzureImageFixture.Build(factory, editing); var source = AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken);
        await foreach (var _ in source) { if (earlyStop) break; }
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in source) { } });
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Creates); Assert.Equal(1, factory.ClientDisposals); Assert.False(image.Disposed);
    }

    /// <summary>枚举端取消令牌也生效；不能只检查创建方法上的令牌。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task EnumerationCancellation_IsForwardedBeforeHttp(bool editing)
    {
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        if (editing)
        {
            var source = new AzureImageEditAdapter(factory, AzureImageFixture.Options()).StreamMultipartEditAsync(AzureImageStreamFixture.Edit(1, [image]), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in source.WithCancellation(cancel.Token)) { } });
        }
        else
        {
            var source = new AzureImageAdapter(factory, AzureImageFixture.Options()).StreamAsync(AzureImageStreamFixture.Generation(), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await foreach (var _ in source.WithCancellation(cancel.Token)) { } });
        }
        Assert.Equal(0, factory.Creates); Assert.Equal(0, image.Consumed);
    }

    /// <summary>原生 n=10 不被缩成单图；无需唯一的虚构图片 ID，也不按 Base64 去重合法输出。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MultipleNativeFinals_ArePreservedUpToDocumentedCount(bool editing)
    {
        var frame = AzureImageStreamFixture.Case(editing).GetProperty("frames")[2].GetProperty("data");
        var wire = string.Concat(Enumerable.Repeat(AzureImageStreamFixture.Frame(frame), 10));
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var generation = AzureImageStreamFixture.Generation(); generation.Parameters["n"] = JsonSerializer.SerializeToElement(10);
        var edit = AzureImageStreamFixture.Edit(1, [image]) with { Fields = [new("n", "10"), new("stream", "true")] };
        var source = editing ? AzureImageStreamFixture.Observe(client.Image.StreamMultipartEditAsync(edit, TestContext.Current.CancellationToken))
            : AzureImageStreamFixture.Observe(client.Image.StreamAsync(generation, TestContext.Current.CancellationToken));
        var seen = new List<AzureImageStreamEvent>(); await foreach (var item in source) seen.Add(item);
        Assert.Equal(10, seen.Count); Assert.All(seen, item => Assert.True(JsonElement.DeepEquals(frame, item.Data)));
        if (editing) { Assert.Equal(1, seen.Count(item => item.Completed == true)); Assert.True(seen[^1].Completed); }
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>显式 partial_images=0 与没有部分图片的原生流保持一致，不生成占位进度事件。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ZeroPartialImages_DeliversOnlyNativeFinal(bool editing)
    {
        var frame = AzureImageStreamFixture.Case(editing).GetProperty("frames")[2].GetProperty("data");
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            var text = Encoding.UTF8.GetString(await message.Content!.ReadAsByteArrayAsync(token));
            if (editing) Assert.Contains("name=partial_images\r\n\r\n0", text);
            else { using var body = JsonDocument.Parse(text); Assert.Equal(0, body.RootElement.GetProperty("partial_images").GetInt32()); }
            return AzureImageStreamFixture.Response(AzureImageStreamFixture.Frame(frame));
        } };
        await using var client = AzureImageFixture.Build(factory, editing); var request = AzureImageStreamFixture.Generation(); request.Parameters["partial_images"] = JsonSerializer.SerializeToElement(0);
        var edit = AzureImageStreamFixture.Edit(1, [image]) with { Fields = [new("partial_images", "0"), new("stream", "true")] };
        var source = editing ? AzureImageStreamFixture.Observe(client.Image.StreamMultipartEditAsync(edit, TestContext.Current.CancellationToken))
            : AzureImageStreamFixture.Observe(client.Image.StreamAsync(request, TestContext.Current.CancellationToken));
        var seen = new List<AzureImageStreamEvent>(); await foreach (var item in source) seen.Add(item);
        Assert.Single(seen); Assert.True(JsonElement.DeepEquals(frame, seen[0].Data)); Assert.Equal(1, factory.Calls);
    }

    /// <summary>SSE 多行 JSON、message 名和 id/retry 继承遵守协议，原始字段/拼接数据保持不变。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MultilineAndInheritedSseIdentity_ArePreserved(bool editing)
    {
        var frames = AzureImageStreamFixture.Case(editing).GetProperty("frames"); var partial = frames[1].GetProperty("raw_data").GetString()!; var cut = partial.IndexOf(',') + 1;
        var logical = partial[..cut] + "\n" + partial[cut..];
        var wire = "id: original-inherited\r\nretry: 0\r\nevent: message\r\ndata: " + partial[..cut] + "\r\ndata: " + partial[cut..]
            + "\r\n\r\n" + AzureImageStreamFixture.Frame(frames[2].GetProperty("data"));
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Equal(2, seen.Count); Assert.Equal(logical, seen[0].Data.GetRawText()); Assert.Equal("message", seen[0].EventName); Assert.Null(seen[1].EventName);
        Assert.All(seen, item => { Assert.Equal("original-inherited", item.EventId); Assert.Equal(0, item.RetryMilliseconds); });
    }
}
