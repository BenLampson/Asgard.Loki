using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>明确的原生协议兼容目标复用现有封装，不按品牌/地址猜测能力，也不创建空供应商包装。</summary>
public sealed class CompatibleImageProfilesTests
{
    /// <summary>原生生成 JSON/SSE 保持完整请求/结果及目标身份，只有预期的实际模型重新绑定。</summary>
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(0, true)] [InlineData(1, true)]
    public async Task GenerationPreservesStandardWireAndOriginalOutcomes(int profileIndex, bool streaming)
    {
        var options = Options(profileIndex); var response = new EditReadStream(System.Text.Encoding.UTF8.GetBytes(streaming ? GenerationFixture.StreamBody : EditFixture.Text("generation-response")));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Identity(message, options, false, streaming); using var actual = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(Wire(streaming ? "generation-stream-wire" : "generation-wire", options.Model, false), actual.RootElement));
            return EditFixture.Response(response, streaming ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8");
        });
        using var factory = new EditTestFactory(handler); await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, options));
        if (streaming)
        {
            var expected = EditFixture.Json("generation-events"); var index = 0;
            await foreach (var item in client.Image.StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken))
            {
                var snapshot = expected[index++]; Assert.True(JsonElement.DeepEquals(snapshot.GetProperty("data"), item.Data));
                if (snapshot.TryGetProperty("raw_json", out var raw)) Assert.Equal(raw.GetString(), item.Data.GetRawText());
                Assert.Equal(snapshot.GetProperty("event_name").GetString(), item.EventName); Assert.Equal(snapshot.GetProperty("event_id").GetString(), item.EventId);
                Assert.Equal(snapshot.GetProperty("retry").GetInt64(), item.RetryMilliseconds); Assert.Equal(new("pictures", options.Model, options.Id), item.Metadata);
                if (item.Type == "image_generation.completed") { Assert.True(response.Eof); Assert.True(response.Disposed); }
            }
            Assert.Equal(expected.GetArrayLength(), index);
        }
        else
        {
            var result = await client.Image.GenerateAsync(GenerationFixture.Request(), TestContext.Current.CancellationToken);
            Assert.True(JsonElement.DeepEquals(EditFixture.Json("generation-response"), result.Data)); Assert.Equal(new("pictures", options.Model, options.Id), result.Metadata);
        }
        Closed(factory, handler, options, response);
    }
    /// <summary>原生 JSON 编辑引用/遮罩/未知字段及 SSE 身份保留，不转换为其他厂商载体。</summary>
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(0, true)] [InlineData(1, true)]
    public async Task JsonEditingPreservesNativeReferencesAndOriginalOutcomes(int profileIndex, bool streaming)
    {
        var options = Options(profileIndex); var response = new EditReadStream(System.Text.Encoding.UTF8.GetBytes(streaming ? EditSseFixture.Body : EditFixture.Text("edit-json-response")));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Identity(message, options, true, streaming); using var actual = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(Wire(streaming ? "edit-json-stream-wire" : "edit-json-wire", options.Model, false), actual.RootElement));
            return EditFixture.Response(response, streaming ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8");
        });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, options));
        if (streaming) await Events(client.Image.StreamEditAsync(EditSseFixture.Json(), TestContext.Current.CancellationToken), options, response);
        else
        {
            var result = await client.Image.EditAsync(EditFixture.Request(), TestContext.Current.CancellationToken);
            Assert.True(JsonElement.DeepEquals(EditFixture.Json("edit-json-response"), result.Data)); Assert.Equal(new("pictures", options.Model, options.Id), result.Metadata);
        }
        Closed(factory, handler, options, response);
    }
    /// <summary>原生 multipart 图片/遮罩角色、顺序、重复文本字段和输入流所有权同样保持。</summary>
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(0, true)] [InlineData(1, true)]
    public async Task MultipartEditingPreservesFullPartsAndBorrowedUploads(int profileIndex, bool streaming)
    {
        var options = Options(profileIndex); using var one = new EditReadStream([0, 255, 128]); using var two = new EditReadStream([13, 10, 42]); using var mask = new EditReadStream([1, 2, 3, 4]);
        var response = new EditReadStream(System.Text.Encoding.UTF8.GetBytes(streaming ? EditSseFixture.Body : EditFixture.Text("edit-json-response")));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Identity(message, options, true, streaming); var actual = await EditMultipartWire.Read(message.Content!, token);
            Assert.True(JsonElement.DeepEquals(Wire(streaming ? "edit-multipart-stream-wire" : "edit-multipart-rich", options.Model, true), actual));
            return EditFixture.Response(response, streaming ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8");
        });
        using var factory = new EditTestFactory(handler); await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, options));
        if (streaming) await Events(client.Image.StreamMultipartEditAsync(EditSseFixture.Multipart(one, two, mask), TestContext.Current.CancellationToken), options, response);
        else
        {
            var result = await client.Image.EditMultipartAsync(EditFixture.Multipart(one, two, mask), TestContext.Current.CancellationToken);
            Assert.True(JsonElement.DeepEquals(EditFixture.Json("edit-json-response"), result.Data)); Assert.Equal(new("pictures", options.Model, options.Id), result.Metadata);
        }
        Assert.All(new[] { one, two, mask }, upload => Assert.False(upload.Disposed)); Closed(factory, handler, options, response);
    }
    /// <summary>保留原始事件 JSON/SSE 元数据，终态只在传输资源完成后交付。</summary>
    private static async Task Events(IAsyncEnumerable<ImageEditEvent> events, OpenAiImageOptions options, EditReadStream response)
    {
        var expected = EditFixture.Json("edit-stream-events"); var index = 0;
        await foreach (var item in events)
        {
            var snapshot = expected[index++]; Assert.True(JsonElement.DeepEquals(snapshot.GetProperty("data"), item.Data));
            if (snapshot.TryGetProperty("raw_json", out var raw)) Assert.Equal(raw.GetString(), item.Data.GetRawText());
            Assert.Equal(snapshot.GetProperty("event_name").GetString(), item.EventName); Assert.Equal(snapshot.GetProperty("event_id").GetString(), item.EventId);
            Assert.Equal(snapshot.GetProperty("retry").GetInt64(), item.RetryMilliseconds); Assert.Equal(snapshot.GetProperty("completed").GetBoolean(), item.IsCompleted);
            Assert.Equal(new("pictures", options.Model, options.Id), item.Metadata); if (item.IsCompleted) { Assert.True(response.Eof); Assert.True(response.Disposed); }
        }
        Assert.Equal(expected.GetArrayLength(), index);
    }
    /// <summary>独立目标样本提供完整配置，无品牌路由规则、模型默认或组织/项目注入。</summary>
    private static OpenAiImageOptions Options(int index)
    {
        var profile = EditFixture.Json("compatible-targets").GetProperty("profiles")[index];
        return new() { Id = profile.GetProperty("id").GetString()!, Model = profile.GetProperty("model").GetString()!, ApiKey = profile.GetProperty("api_key").GetString()!,
            BaseAddress = new(profile.GetProperty("base_address").GetString()!), HttpClientName = profile.GetProperty("http_client_name").GetString()! };
    }
    /// <summary>期望请求来自独立完整样本，仅替换明示的目标模型，不使用生产序列化器计算期望。</summary>
    private static JsonElement Wire(string name, string model, bool multipart)
    {
        var value = JsonNode.Parse(EditFixture.Text(name))!;
        if (multipart) value[0]!["text"] = model; else value["model"] = model;
        return JsonSerializer.SerializeToElement(value);
    }
    private static void Identity(HttpRequestMessage message, OpenAiImageOptions options, bool edit, bool streaming)
    {
        Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(options.BaseAddress.AbsoluteUri + (edit ? "images/edits" : "images/generations"), message.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal(options.ApiKey, message.Headers.Authorization.Parameter);
        Assert.False(message.Headers.Contains("OpenAI-Organization")); Assert.False(message.Headers.Contains("OpenAI-Project"));
        if (streaming) Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType);
        else Assert.Empty(message.Headers.Accept);
    }
    private static void Closed(EditTestFactory factory, StubHttpHandler handler, OpenAiImageOptions options, EditReadStream response)
    {
        Assert.Equal(1, handler.Calls); Assert.Equal(options.HttpClientName, Assert.Single(factory.Names)); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.False(factory.Disposed); Assert.True(response.Eof); Assert.True(response.Disposed);
    }
}
