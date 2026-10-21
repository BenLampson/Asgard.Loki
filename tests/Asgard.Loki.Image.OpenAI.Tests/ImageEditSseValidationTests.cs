using System.Text.Json;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.Image.OpenAI.Tests;

/// <summary>原生请求传输冲突、终态/类型/数值的协议边界，不靠容错修复让样本通过。</summary>
public sealed class ImageEditSseValidationTests
{
    /// <summary>JSON 与 multipart 的显式 false 都必须在派发前拒绝，不能暗中改为 true。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ExplicitFalse_FailsBeforeHttp(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options); var request = EditSseFixture.Json(); request.Parameters["stream"] = JsonSerializer.SerializeToElement(false);
        Assert.Throws<ArgumentException>(() => multipart ? adapter.StreamMultipartEditAsync(EditFixture.Multipart(one, two, mask), TestContext.Current.CancellationToken)
            : adapter.StreamEditAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls); Assert.All(new[] { one, two, mask }, value => Assert.Equal(0, value.Reads));
    }
    /// <summary>流式入口同样拒绝坏开关、重复单值和绑定覆盖，不对请求实施隐式修复。</summary>
    [Theory] [InlineData(false, "null")] [InlineData(false, "string")] [InlineData(false, "override")]
    [InlineData(true, "invalid")] [InlineData(true, "duplicate")] [InlineData(true, "override")]
    public void InvalidStreamInput_DoesNotDispatch(bool multipart, string kind)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options); var request = EditSseFixture.Json();
        if (kind == "override") request.Parameters["model"] = JsonSerializer.SerializeToElement("hidden");
        else request.Parameters["stream"] = kind == "null" ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement("true");
        var upload = EditSseFixture.Multipart(one, two, mask) with { Fields = kind switch
        { "duplicate" => [new("stream", "true"), new("stream", "true")], "override" => [new("mask", "hidden")], _ => [new("stream", "TRUE")] } };
        Assert.ThrowsAny<ArgumentException>(() => multipart ? adapter.StreamMultipartEditAsync(upload, TestContext.Current.CancellationToken) : adapter.StreamEditAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(factory.Clients); Assert.All(new[] { one, two, mask }, value => Assert.Equal(0, value.Reads));
    }
    /// <summary>预取消既可发生在调用入口，也可只在枚举 token 中传入；均无 HTTP/上传副作用。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task PreCancelled_BeforeMethodOrEnumeration(bool multipart, bool enumerationOnly)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); using var factory = new EditTestFactory(handler);
        var adapter = new OpenAiImageEditAdapter(factory, EditFixture.Options);
        if (!enumerationOnly) Assert.ThrowsAny<OperationCanceledException>(() => EditSseFixture.Source(adapter, multipart, one, two, mask, cancellation.Token));
        else
        {
            var source = EditSseFixture.Source(adapter, multipart, one, two, mask, CancellationToken.None);
            await using var iterator = source.GetAsyncEnumerator(cancellation.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        }
        Assert.Empty(factory.Clients); Assert.All(new[] { one, two, mask }, value => Assert.Equal(0, value.Reads));
    }
    /// <summary>原生终态序列和 SSE/JSON 类型一致性失败时不得先公开完成事件。</summary>
    [Theory]
    [InlineData(false, "empty")] [InlineData(true, "empty")] [InlineData(false, "partial-only")] [InlineData(true, "partial-only")]
    [InlineData(false, "duplicate")] [InlineData(true, "duplicate")] [InlineData(false, "late")] [InlineData(true, "late")]
    [InlineData(false, "unknown-completed")] [InlineData(true, "unknown-completed")] [InlineData(false, "wrong-family")] [InlineData(true, "wrong-family")]
    [InlineData(false, "name-mismatch")] [InlineData(true, "name-mismatch")] [InlineData(false, "null-type")] [InlineData(true, "null-type")]
    [InlineData(false, "missing-type")] [InlineData(true, "missing-type")] [InlineData(false, "nonobject")] [InlineData(true, "nonobject")]
    [InlineData(false, "duplicate-field")] [InlineData(true, "duplicate-field")] [InlineData(false, "truncated-frame")] [InlineData(true, "truncated-frame")]
    [InlineData(false, "truncated-tail")] [InlineData(true, "truncated-tail")]
    public async Task InvalidSequence_WithholdsCompletion(bool multipart, string kind)
    {
        var body = kind switch
        {
            "empty" => "", "partial-only" => EditSseFixture.Partial, "duplicate" => EditSseFixture.Completed + EditSseFixture.Completed,
            "late" => EditSseFixture.Completed + EditSseFixture.Partial, "unknown-completed" => "data: {\"type\":\"vendor.completed\",\"b64_json\":\"AQID\"}\n\n",
            "wrong-family" => "data: {\"type\":\"image_generation.completed\",\"b64_json\":\"AQID\"}\n\n",
            "name-mismatch" => "event: image_edit.completed\n" + EditSseFixture.Partial,
            "null-type" => "data: {\"type\":null}\n\n", "missing-type" => "data: {\"future\":true}\n\n", "nonobject" => "data: []\n\n",
            "duplicate-field" => "data: {\"type\":\"image_edit.completed\",\"b64_json\":\"AQID\",\"b64_json\":\"BAUG\"}\n\n",
            "truncated-tail" => EditSseFixture.Completed + "data: {\"type\":\"vendor.future\"}",
            _ => EditSseFixture.Completed.TrimEnd('\n')
        };
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response(body))); using var factory = new EditTestFactory(handler);
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(factory, EditFixture.Options)); List<ImageEditEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var item in EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>通用 DONE 或损坏 JSON 不会结束原生编辑流，更不能覆盖已暂存的完成项。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task InvalidJsonOrDone_IsNotSuccessfulTermination(bool multipart, bool afterCompleted)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        var body = (afterCompleted ? EditSseFixture.Completed : EditSseFixture.Partial) + "data: [DONE]\n\n";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response(body)));
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options)); List<ImageEditEvent> seen = [];
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        { await foreach (var item in EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.All(seen, item => Assert.False(item.IsCompleted)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>完成项之后损坏的 JSON 数据仍须消费并失败，不能提前退出流解析。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MalformedJsonAfterTerminal_IsNotIgnored(bool multipart)
    {
        using var one = new EditReadStream([1]); using var two = new EditReadStream([2]); using var mask = new EditReadStream([3]);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response(EditSseFixture.Completed + "data: {\n\n")));
        await using var client = EditFixture.Build(new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options)); List<ImageEditEvent> seen = [];
        await Assert.ThrowsAnyAsync<JsonException>(async () =>
        { await foreach (var item in EditSseFixture.Source(client, multipart, one, two, mask, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Empty(seen);
    }
    /// <summary>已知图片事件缺失/错误字段不能制造成功；未知事件不套用这些形状要求。</summary>
    [Theory]
    [InlineData("{\"type\":\"image_edit.completed\"}")] [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":false}")]
    [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":\" \"}")] [InlineData("{\"type\":\"image_edit.partial_image\",\"b64_json\":\"AQID\"}")]
    [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":\"AQID\",\"quality\":1}")]
    [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":\"AQID\",\"background\":false}")]
    [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":\"AQID\",\"size\":[]}")]
    [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":\"AQID\",\"output_format\":{}}")]
    [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":\"AQID\",\"usage\":1}")]
    [InlineData("{\"type\":\"image_edit.completed\",\"b64_json\":\"AQID\",\"usage\":{\"input_tokens_details\":[]}}")]
    public async Task InvalidKnownShape_IsRejected(string raw)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response("data: " + raw + "\n\n")));
        var source = new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options).StreamEditAsync(EditSseFixture.Json(), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => EditSseFixture.Collect(source)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>小数/指数形式的数学整数不被强制转窄或舍入，JSON 原文保持不变。</summary>
    [Theory]
    [InlineData("1.0")] [InlineData("0.1e1")] [InlineData("1e99999")] [InlineData("-0.0e-99999")]
    [InlineData("900719925474099312345")] [InlineData("1.00000000000000000000000000000")]
    public async Task ExactNativeIntegerForms_ArePreserved(string raw)
    {
        var json = "{\"type\":\"image_edit.partial_image\",\"partial_image_index\":" + raw + ",\"b64_json\":\"AQID\",\"created_at\":" + raw
            + ",\"usage\":{\"input_tokens\":" + raw + ",\"input_tokens_details\":{\"image_tokens\":" + raw + "},\"future\":null}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response("data: " + json + "\n\n" + EditSseFixture.Completed)));
        var result = await EditSseFixture.Collect(new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options).StreamEditAsync(EditSseFixture.Json(), TestContext.Current.CancellationToken));
        Assert.Equal(json, result[0].Data.GetRawText()); Assert.True(result[^1].IsCompleted);
    }
    /// <summary>以不同已知位置覆盖非负整数检查，极小负数和近整数都不能因浮点精度而通过。</summary>
    public static IEnumerable<object[]> InvalidNumbers
    {
        get
        {
            foreach (var position in new[] { "partial_image_index", "created_at", "usage", "details" })
                foreach (var raw in new[] { "-1e-99999", "1.00000000000000000000000000001", "0.99999999999999999999999999999", "null", "\"1\"" })
                    yield return [position, raw];
        }
    }
    /// <summary>计数验证不分配巨大指数对应的字符串，不从 double 近似推断整数性。</summary>
    [Theory] [MemberData(nameof(InvalidNumbers))]
    public async Task InvalidNativeNumbers_AreRejected(string field, string raw)
    {
        var addition = field switch { "usage" => "\"usage\":{\"input_tokens\":" + raw + "}", "details" => "\"usage\":{\"input_tokens_details\":{\"image_tokens\":" + raw + "}}", _ => "\"" + field + "\":" + raw };
        var json = "{\"type\":\"image_edit.partial_image\",\"b64_json\":\"AQID\"," + (field == "partial_image_index" ? "" : "\"partial_image_index\":0,") + addition + "}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(EditSseFixture.Response("data: " + json + "\n\n" + EditSseFixture.Completed)));
        await Assert.ThrowsAsync<InvalidDataException>(() => EditSseFixture.Collect(new OpenAiImageEditAdapter(new StubHttpClientFactory(handler), EditFixture.Options).StreamEditAsync(EditSseFixture.Json(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Calls);
    }
}
