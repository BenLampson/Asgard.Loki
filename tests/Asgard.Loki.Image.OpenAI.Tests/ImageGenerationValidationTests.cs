using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>生成调用的输入及原生事件边界，不通过静默覆盖、容错补终态或跨编辑事件匹配来成功。</summary>
public sealed class ImageGenerationValidationTests
{
    /// <summary>非流式 true、流式 false 必须显式失败，不改写调用者控制字段。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TransportConflict_IsRejectedBeforeHttp(bool streaming)
    {
        var request = GenerationFixture.Request(streaming); request.Parameters["stream"] = JsonSerializer.SerializeToElement(!streaming);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageAdapter(factory, GenerationFixture.Options);
        if (streaming) Assert.Throws<ArgumentException>(() => adapter.StreamAsync(request, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<NotSupportedException>(() => adapter.GenerateAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>所有非法绑定/JSON 参数在创建 HTTP 客户端前失败，直接调用不依赖路由已经校验。</summary>
    [Theory] [InlineData("null")] [InlineData("model")] [InlineData("prompt")] [InlineData("parameters")]
    [InlineData("override-model")] [InlineData("override-prompt")] [InlineData("undefined")] [InlineData("stream-null")] [InlineData("stream-string")] [InlineData("utf8")]
    public async Task InvalidRequest_FailsBeforeHttp(string kind)
    {
        var request = GenerationFixture.Request();
        switch (kind)
        {
            case "null": request = null!; break;
            case "model": request = request with { Model = "" }; break;
            case "prompt": request = request with { Prompt = "" }; break;
            case "parameters": request = request with { Parameters = null! }; break;
            case "utf8": request = request with { Prompt = "\ud800" }; break;
            case "undefined": request.Parameters["future"] = default; break;
            default: request.Parameters[kind switch { "override-model" => "model", "override-prompt" => "prompt", _ => "stream" }]
                = kind == "stream-null" ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement("value"); break;
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new OpenAiImageAdapter(factory, GenerationFixture.Options).GenerateAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients);
    }
    /// <summary>预取消可在非流式、流式方法入口或只通过枚举 token 提供，均不派发。</summary>
    [Theory] [InlineData("json")] [InlineData("stream")] [InlineData("enumerator")]
    public async Task PreCancellation_DoesNotCreateClient(string kind)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        using var factory = new EditTestFactory(handler); var adapter = new OpenAiImageAdapter(factory, GenerationFixture.Options);
        if (kind == "json") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GenerateAsync(GenerationFixture.Request(), cancellation.Token));
        else if (kind == "stream") Assert.ThrowsAny<OperationCanceledException>(() => adapter.StreamAsync(GenerationFixture.Request(true), cancellation.Token));
        else
        {
            await using var iterator = adapter.StreamAsync(GenerationFixture.Request(true), CancellationToken.None).GetAsyncEnumerator(cancellation.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        }
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>原生缺终态、重复/晚到/截断/错误家族事件不能产生成功终态。</summary>
    [Theory]
    [InlineData("empty")] [InlineData("partial-only")] [InlineData("duplicate")] [InlineData("late")] [InlineData("wrong-family")]
    [InlineData("future-completed")] [InlineData("truncated-final")] [InlineData("truncated-tail")] [InlineData("missing-type")]
    [InlineData("null-type")] [InlineData("name-mismatch")] [InlineData("duplicate-field")] [InlineData("nonobject")]
    public async Task InvalidSequence_WithholdsNativeCompleted(string kind)
    {
        var body = kind switch
        {
            "empty" => "", "partial-only" => GenerationFixture.Partial, "duplicate" => GenerationFixture.Completed + GenerationFixture.Completed,
            "late" => GenerationFixture.Completed + GenerationFixture.Partial, "wrong-family" => EditSseFixture.Completed,
            "future-completed" => "data: {\"type\":\"vendor.completed\",\"future\":null}\n\n", "truncated-final" => GenerationFixture.Completed.TrimEnd('\n'),
            "truncated-tail" => GenerationFixture.Completed + "data: {\"type\":\"vendor.future\"}", "missing-type" => "data: {\"future\":true}\n\n",
            "null-type" => "data: {\"type\":null}\n\n", "name-mismatch" => "event: image_generation.completed\n" + GenerationFixture.Partial,
            "duplicate-field" => "data: {\"type\":\"image_generation.completed\",\"b64_json\":\"AQID\",\"b64_json\":\"BAUG\"}\n\n", _ => "data: []\n\n"
        };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(true, body))); using var factory = new EditTestFactory(handler);
        await using var client = GenerationFixture.Build(new OpenAiImageAdapter(factory, GenerationFixture.Options)); List<ImageGenerationEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var item in client.Image.StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Type == "image_generation.completed"); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>通用 DONE 和损坏 JSON 即使位于完成之后也不能被提前 break 忽略。</summary>
    [Theory] [InlineData("done-only")] [InlineData("done-tail")] [InlineData("malformed-tail")]
    public async Task DoneOrMalformedTail_DoesNotTerminateSuccessfully(string kind)
    {
        var body = (kind == "done-only" ? "" : GenerationFixture.Completed) + (kind == "malformed-tail" ? "data: {\n\n" : "data: [DONE]\n\n");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(true, body)));
        await using var client = GenerationFixture.Build(new OpenAiImageAdapter(new StubHttpClientFactory(handler), GenerationFixture.Options)); List<ImageGenerationEvent> seen = [];
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        { await foreach (var item in client.Image.StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Empty(seen); Assert.Equal(1, handler.Calls);
    }
    /// <summary>已知生成事件必须验证原生字段，不能借用编辑完成或把数值近似取整。</summary>
    [Theory]
    [InlineData("{\"type\":\"image_generation.completed\"}")]
    [InlineData("{\"type\":\"image_generation.completed\",\"b64_json\":false}")]
    [InlineData("{\"type\":\"image_generation.completed\",\"b64_json\":\"AQID\",\"quality\":1}")]
    [InlineData("{\"type\":\"image_generation.completed\",\"b64_json\":\"AQID\",\"usage\":[]}")]
    [InlineData("{\"type\":\"image_generation.completed\",\"b64_json\":\"AQID\",\"usage\":{\"output_tokens\":-1e-9999}}")]
    [InlineData("{\"type\":\"image_generation.partial_image\",\"b64_json\":\"AQID\",\"partial_image_index\":1.00000000000000000000000000001}")]
    [InlineData("{\"type\":\"image_generation.partial_image\",\"b64_json\":\"AQID\",\"partial_image_index\":-1e-9999}")]
    public async Task InvalidKnownEventShape_IsRejected(string raw)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(true, "data: " + raw + "\n\n")));
        await Assert.ThrowsAsync<InvalidDataException>(() => GenerationFixture.Collect(new OpenAiImageAdapter(new StubHttpClientFactory(handler), GenerationFixture.Options).StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken)));
    }
    /// <summary>嵌套重复参数不是按末值覆盖；数学整数形式和大整数原文则不缩窄。</summary>
    [Fact]
    public async Task DuplicateInputFails_ExactIntegerEventSurvives()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(GenerationFixture.Response(true,
            "data: {\"type\":\"image_generation.partial_image\",\"partial_image_index\":900719925474099312345,\"b64_json\":\"AQID\",\"created_at\":0.1e1}\n\n" + GenerationFixture.Completed)));
        using var factory = new EditTestFactory(handler); var adapter = new OpenAiImageAdapter(factory, GenerationFixture.Options); var request = GenerationFixture.Request(true);
        using var duplicate = JsonDocument.Parse("{\"x\":1,\"x\":2}"); request.Parameters["future"] = duplicate.RootElement;
        Assert.Throws<InvalidDataException>(() => adapter.StreamAsync(request, TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients);
        var seen = await GenerationFixture.Collect(adapter.StreamAsync(GenerationFixture.Request(true), TestContext.Current.CancellationToken));
        Assert.Equal("900719925474099312345", seen[0].Data.GetProperty("partial_image_index").GetRawText()); Assert.Equal("0.1e1", seen[0].Data.GetProperty("created_at").GetRawText());
    }
}
