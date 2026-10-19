using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>实际流式 JSON/MIME 请求与完整原生事件序列的独立样本快照。</summary>
public sealed class ImageEditSseWireTests
{
    /// <summary>支持任意 UTF-8/事件分片；部分图片和未知事件即时公开，原始完成项等待资源释放。</summary>
    [Theory]
    [InlineData(false, 1, false)] [InlineData(true, 1, false)] [InlineData(false, 7, true)] [InlineData(true, 7, true)]
    [InlineData(false, 4096, false)] [InlineData(true, 4096, false)]
    public async Task Stream_PreservesFullWireEventsAndDelayedTerminal(bool multipart, int fragment, bool explicitTrue)
    {
        using var one = new EditReadStream([0, 255, 128]); using var two = new EditReadStream([13, 10, 42]); using var mask = new EditReadStream([1, 2, 3, 4]);
        var bytes = Encoding.UTF8.GetBytes(EditSseFixture.Body); var stream = new EditReadStream(bytes) { FragmentSize = fragment };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://images.invalid/tenant/v1/images/edits", message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("fixture-image-secret", message.Headers.Authorization!.Parameter); Assert.Equal("org-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Organization")));
            Assert.Equal("project-fixture", Assert.Single(message.Headers.GetValues("OpenAI-Project"))); Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType);
            JsonElement actual;
            if (multipart) actual = await EditMultipartWire.Read(message.Content!, token);
            else { Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); using var document = JsonDocument.Parse(await message.Content.ReadAsByteArrayAsync(token)); actual = document.RootElement.Clone(); }
            Assert.True(JsonElement.DeepEquals(EditFixture.Json(multipart ? "edit-multipart-stream-wire" : "edit-json-stream-wire"), actual), actual.GetRawText());
            return EditFixture.Response(stream, "Text/Event-Stream; charset=\"UTF-8\"", bytes.Length);
        });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        var originalJson = EditSseFixture.Json(explicitTrue); var originalMultipart = EditSseFixture.Multipart(one, two, mask, explicitTrue);
        var source = multipart ? client.Image.StreamMultipartEditAsync(originalMultipart, TestContext.Current.CancellationToken)
            : client.Image.StreamEditAsync(originalJson, TestContext.Current.CancellationToken);
        Assert.Equal(explicitTrue, originalJson.Parameters.ContainsKey("stream")); Assert.Equal(explicitTrue ? 1 : 0, originalMultipart.Fields.Count(f => f.Name == "stream"));
        List<ImageEditEvent> result = [];
        await foreach (var item in source)
        {
            if (item.IsCompleted) { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
            else { Assert.False(stream.Disposed); Assert.False(Assert.Single(factory.Clients).Disposed); }
            result.Add(item);
        }
        var expected = EditFixture.Json("edit-stream-events"); Assert.Equal(expected.GetArrayLength(), result.Count);
        for (var index = 0; index < result.Count; index++)
        {
            var snapshot = expected[index]; var item = result[index]; Assert.True(JsonElement.DeepEquals(snapshot.GetProperty("data"), item.Data));
            if (snapshot.TryGetProperty("raw_json", out var raw)) Assert.Equal(raw.GetString(), item.Data.GetRawText());
            Assert.Equal(snapshot.GetProperty("event_name").GetString(), item.EventName); Assert.Equal(snapshot.GetProperty("event_id").GetString(), item.EventId);
            Assert.Equal(snapshot.GetProperty("retry").GetInt64(), item.RetryMilliseconds); Assert.Equal(snapshot.GetProperty("completed").GetBoolean(), item.IsCompleted);
            Assert.Equal(new("pictures", "native-image-model", "edit-target-one"), item.Metadata);
        }
        Assert.Equal("0.123456789012345678901", result[^1].Data.GetProperty("future").GetProperty("decimal").GetRawText());
        Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed)); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>请求在直接调用时快照，但不枚举就不创建 HTTP 内容/客户端或读取上传。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DirectStream_SnapshotsBeforeEnumeration(bool multipart)
    {
        using var one = new EditReadStream([0, 255, 128]); using var two = new EditReadStream([13, 10, 42]); using var mask = new EditReadStream([1, 2, 3, 4]);
        var json = EditSseFixture.Json(); using var ownedDocument = JsonDocument.Parse(EditFixture.Text("edit-json-input"));
        json.Parameters["images"] = ownedDocument.RootElement.GetProperty("images");
        var original = EditSseFixture.Multipart(one, two, mask); var images = original.Images.ToList(); var fields = original.Fields.ToList();
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            JsonElement actual;
            if (multipart) actual = await EditMultipartWire.Read(message.Content!, token);
            else { using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); actual = document.RootElement.Clone(); }
            Assert.True(JsonElement.DeepEquals(EditFixture.Json(multipart ? "edit-multipart-stream-wire" : "edit-json-stream-wire"), actual)); return EditSseFixture.Response();
        });
        using var factory = new EditTestFactory(handler); var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        var source = multipart ? adapter.StreamMultipartEditAsync(original with { Images = images, Fields = fields }, TestContext.Current.CancellationToken) : adapter.StreamEditAsync(json, TestContext.Current.CancellationToken);
        ownedDocument.Dispose(); json.Parameters.Clear(); images.Clear(); fields.Clear(); Assert.Empty(factory.Clients); Assert.Equal(0, one.Reads);
        Assert.True((await EditSseFixture.Collect(source))[^1].IsCompleted); Assert.Equal(1, handler.Calls);
    }
    /// <summary>每次流式调用只能枚举一次，不因复用 enumerable 在文件尾部再次上传。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RepeatEnumeration_DoesNotReplay(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response())); using var factory = new EditTestFactory(handler);
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options));
        var source = EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken);
        await EditSseFixture.Collect(source); await Assert.ThrowsAsync<InvalidOperationException>(() => EditSseFixture.Collect(source)); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }
    /// <summary>没有部分图片也可以原生完成，不按请求 partial_images 数量强行补造/要求事件。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompletedOnly_IsNativeSuccess(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response(EditSseFixture.Completed)));
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options));
        var result = await EditSseFixture.Collect(EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken));
        Assert.True(Assert.Single(result).IsCompleted); Assert.Equal(1, handler.Calls);
    }
}
