using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>原生生成 JSON/SSE 的完整独立请求/响应快照，经公开 SDK 与实际 HTTP 路径执行。</summary>
public sealed class ImageGenerationWireTests
{
    /// <summary>省略与显式 false 保持原样，完整未知值/图片顺序/用量原文保留到源和客户端释放之后。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CompleteJson_PreservesNativeWireAndFullResult(bool explicitFalse)
    {
        var request = GenerationFixture.Request(); if (explicitFalse) request.Parameters["stream"] = JsonSerializer.SerializeToElement(false);
        var stream = new EditReadStream(Encoding.UTF8.GetBytes(EditFixture.Text("generation-response")));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Identity(message, false); using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            var expected = EditFixture.Json("generation-wire");
            foreach (var property in expected.EnumerateObject()) Assert.True(JsonElement.DeepEquals(property.Value, document.RootElement.GetProperty(property.Name)));
            Assert.Equal(expected.EnumerateObject().Count() + (explicitFalse ? 1 : 0), document.RootElement.EnumerateObject().Count());
            Assert.Equal(explicitFalse, document.RootElement.TryGetProperty("stream", out var value)); if (explicitFalse) Assert.False(value.GetBoolean());
            return EditFixture.Response(stream, "application/json; charset=utf-8");
        });
        using var factory = new EditTestFactory(handler); await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options));
        var result = await client.Image.GenerateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(EditFixture.Text("generation-response").Trim(), result.Data.GetRawText()); Assert.Equal(new byte[] { 3, 4, 5, 6 }, result.Images[0].Decode().ToArray());
        Assert.Equal("https://cdn.invalid/generated.webp", result.Images[1].Url); Assert.Equal("原生生成提示词 😀", result.Images[0].Data.GetProperty("revised_prompt").GetString());
        Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
        Assert.Equal(new("pictures", "native-generation-model", "generation-target"), result.Metadata); Assert.Equal("pictures", request.Model);
    }
    /// <summary>分片不破坏 UTF-8/多行 JSON/独立 SSE 身份，终态只在 EOF 和全部传输资源释放后可见。</summary>
    [Theory] [InlineData(1, false)] [InlineData(7, true)] [InlineData(4096, false)]
    public async Task Streaming_MatchesFullRequestAndEventGoldens(int fragment, bool explicitTrue)
    {
        var bytes = Encoding.UTF8.GetBytes(GenerationFixture.StreamBody); var stream = new EditReadStream(bytes) { FragmentSize = fragment };
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Identity(message, true); using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(EditFixture.Json("generation-stream-wire"), document.RootElement));
            return EditFixture.Response(stream, "Text/Event-Stream; charset=\"UTF-8\"", bytes.Length);
        });
        using var factory = new EditTestFactory(handler); await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options));
        var request = GenerationFixture.Request(true); if (explicitTrue) request.Parameters["stream"] = JsonSerializer.SerializeToElement(true);
        var expected = EditFixture.Json("generation-events"); List<ImageGenerationEvent> seen = [];
        await foreach (var item in client.Image.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            if (item.Type == "image_generation.completed") { Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
            else { Assert.False(stream.Disposed); Assert.False(Assert.Single(factory.Clients).Disposed); }
            var snapshot = expected[seen.Count]; Assert.True(JsonElement.DeepEquals(snapshot.GetProperty("data"), item.Data));
            if (snapshot.TryGetProperty("raw_json", out var raw)) Assert.Equal(raw.GetString(), item.Data.GetRawText());
            Assert.Equal(snapshot.GetProperty("event_name").GetString(), item.EventName); Assert.Equal(snapshot.GetProperty("event_id").GetString(), item.EventId);
            Assert.Equal(snapshot.GetProperty("retry").GetInt64(), item.RetryMilliseconds); Assert.Equal(new("pictures", "native-generation-model", "generation-target"), item.Metadata);
            seen.Add(item);
        }
        Assert.Equal(expected.GetArrayLength(), seen.Count); Assert.Equal(explicitTrue, request.Parameters.ContainsKey("stream"));
        Assert.Equal("0.123456789012345678901", seen[^1].Data.GetProperty("future").GetProperty("decimal").GetRawText());
        Assert.Equal("fixture-generation", Assert.Single(factory.Names)); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>公开 SDK 与直接适配器都在调用时复制 JSON，流式不能等到枚举才读取已释放的源文档。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Snapshot_PrecedesFactoryAndEnumeration(bool streaming, bool direct)
    {
        var request = GenerationFixture.Request(streaming); using var document = JsonDocument.Parse(EditFixture.Text("generation-input")); request.Parameters["future"] = document.RootElement.GetProperty("future");
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            using var actual = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(EditFixture.Json(streaming ? "generation-stream-wire" : "generation-wire"), actual.RootElement));
            return GenerationFixture.Response(streaming);
        });
        using var factory = new EditTestFactory(handler) { OnCreate = () => { document.Dispose(); request.Parameters.Clear(); } };
        var adapter = new OpenAiImageAdapter(factory, GenerationFixture.Options); await using var client = GenerationFixture.Build(adapter);
        if (streaming)
        {
            var source = direct ? adapter.StreamAsync(request, TestContext.Current.CancellationToken) : client.Image.StreamAsync(request, TestContext.Current.CancellationToken);
            document.Dispose(); request.Parameters.Clear(); Assert.Empty(factory.Clients); Assert.Equal("image_generation.completed", (await GenerationFixture.Collect(source))[^1].Type);
        }
        else await (direct ? adapter.GenerateAsync(request, TestContext.Current.CancellationToken) : client.Image.GenerateAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }
    /// <summary>完成项可独立出现，不按请求数量补造部分图像；同一 enumerable 不能重复执行。</summary>
    [Fact]
    public async Task CompletedOnlyAndSingleEnumeration_DoNotInventOrReplay()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(true, GenerationFixture.Completed))); using var factory = new EditTestFactory(handler);
        await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options));
        var source = client.Image.StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken);
        Assert.Equal("image_generation.completed", Assert.Single(await GenerationFixture.Collect(source)).Type);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GenerationFixture.Collect(source)); Assert.Single(factory.Clients); Assert.Equal(1, handler.Calls);
    }
    /// <summary>生成与编辑交错消费只复用纯字段校验，不能共享终态、目标元数据或响应资源。</summary>
    [Fact]
    public async Task InterleavedGenerationAndEdit_KeepIndependentNativeState()
    {
        var generationStream = new EditReadStream(Encoding.UTF8.GetBytes(GenerationFixture.StreamBody)) { FragmentSize = 1 };
        var editStream = new EditReadStream(Encoding.UTF8.GetBytes(EditSseFixture.Body)) { FragmentSize = 1 };
        using var handler = new StubHttpHandler((message, _) => Task.FromResult(EditFixture.Response(
            message.RequestUri!.AbsolutePath.EndsWith("/generations", StringComparison.Ordinal) ? generationStream : editStream, "text/event-stream")));
        using var factory = new EditTestFactory(handler); var generation = new OpenAiImageAdapter(factory, GenerationFixture.Options); var edit = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        await using var client = new LokiClientBuilder().AddImage(new()
        { Routes = [new() { Model = "pictures", Targets = [generation] }], EditRoutes = [new() { Model = "pictures", Targets = [edit] }] }).Build();
        await using var one = client.Image.StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using var two = client.Image.StreamEditAsync(EditSseFixture.Json(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await one.MoveNextAsync()); Assert.Equal("image_generation.partial_image", one.Current.Type);
        Assert.True(await two.MoveNextAsync()); Assert.False(two.Current.IsCompleted);
        while (await one.MoveNextAsync()) Assert.Equal("generation-target", one.Current.Metadata.ProviderId);
        Assert.True(generationStream.Disposed); Assert.False(editStream.Disposed); Assert.True(factory.Clients[0].Disposed); Assert.False(factory.Clients[1].Disposed);
        while (await two.MoveNextAsync()) Assert.Equal("edit-target-one", two.Current.Metadata.ProviderId);
        Assert.True(editStream.Disposed); Assert.True(factory.Clients[1].Disposed); Assert.Equal(2, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>请求端点、完整目标身份和内容协商仅由显式生成配置决定。</summary>
    private static void Identity(HttpRequestMessage message, bool streaming)
    {
        Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://images.invalid/tenant/v1/images/generations", message.RequestUri!.AbsoluteUri);
        Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); Assert.Equal("fixture-generation-secret", message.Headers.Authorization!.Parameter);
        Assert.Equal("org-generation", Assert.Single(message.Headers.GetValues("OpenAI-Organization"))); Assert.Equal("project-generation", Assert.Single(message.Headers.GetValues("OpenAI-Project")));
        if (streaming) Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType); else Assert.Empty(message.Headers.Accept);
    }
}
