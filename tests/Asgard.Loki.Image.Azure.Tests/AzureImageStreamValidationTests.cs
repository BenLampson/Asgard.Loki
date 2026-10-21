using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>原生流的请求/JSON/状态关联边界不能被伪造完成、错误操作或近整数舍入绕过。</summary>
public sealed class AzureImageStreamValidationTests
{
    /// <summary>准确的流字段及原生计数在 HTTP 前验证，不能覆盖显式 false 或默默缩成单图。</summary>
    [Theory]
    [InlineData("false")] [InlineData("alias")] [InlineData("count-alias")] [InlineData("count-zero")] [InlineData("count-many")]
    [InlineData("count-string")] [InlineData("count-null")] [InlineData("near-integer")] [InlineData("negative-small")]
    [InlineData("partial-negative")] [InlineData("partial-many")] [InlineData("partial-alias")]
    public void InvalidGenerationStreamRequest_FailsBeforeHttp(string kind)
    {
        using var factory = new AzureImageTestFactory(); var request = AzureImageStreamFixture.Generation();
        var name = kind switch { "alias" => "Stream", "count-alias" => "N", "partial-alias" => "Partial_Images",
            "false" => "stream", "partial-negative" or "partial-many" => "partial_images", _ => "n" };
        var raw = kind switch { "false" => "false", "count-zero" => "0", "count-many" => "11", "count-string" => "\"1\"", "count-null" => "null",
            "near-integer" => "1.00000000000000000000000000001", "negative-small" => "-1e-1000", "partial-negative" => "-1", "partial-many" => "4", "alias" => "true", _ => "1" };
        using var document = JsonDocument.Parse(raw); request.Parameters[name] = document.RootElement.Clone();
        Assert.ThrowsAny<ArgumentException>(() => new AzureImageAdapter(factory, AzureImageFixture.Options()).StreamAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Creates);
    }

    /// <summary>编辑使用原生整数文本，流开关/单值字段不能重复或被隐式覆盖。</summary>
    [Theory] [InlineData("false")] [InlineData("count-alias")] [InlineData("count-zero")] [InlineData("count-many")]
    [InlineData("count-fraction")] [InlineData("partial-many")] [InlineData("partial-negative")] [InlineData("duplicate")]
    public void InvalidEditStreamRequest_FailsBeforeUpload(string kind)
    {
        using var upload = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory();
        var name = kind switch { "false" => "stream", "count-alias" => "N", "partial-many" or "partial-negative" => "partial_images", _ => "n" };
        var value = kind switch { "false" => "false", "count-zero" => "0", "count-many" => "11", "count-fraction" => "1.0", "partial-many" => "4", "partial-negative" => "-1", _ => "1" };
        var request = AzureImageStreamFixture.Edit(1, [upload]) with { Fields = kind == "duplicate" ? [new("n", "1"), new("n", "2")] : [new(name, value)] };
        Assert.ThrowsAny<ArgumentException>(() => new AzureImageEditAdapter(factory, AzureImageFixture.Options()).StreamMultipartEditAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Creates); Assert.Equal(0, upload.Consumed); Assert.False(upload.Disposed);
    }

    /// <summary>数学整数的小数/指数原文保持不变，巨大索引/用量不会因中间浮点丢失精度。</summary>
    [Theory] [InlineData("1.0")] [InlineData("1e0")] [InlineData("0.1e1")]
    public async Task ExactIntegerRequest_IsPreservedWithoutRewriting(string raw)
    {
        using var document = JsonDocument.Parse(raw); var request = AzureImageStreamFixture.Generation(); request.Parameters["n"] = document.RootElement.Clone();
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            using var actual = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token)); Assert.Equal(raw, actual.RootElement.GetProperty("n").GetRawText());
            return AzureImageStreamFixture.Response(AzureImageStreamFixture.Wire(false));
        } };
        await foreach (var _ in new AzureImageAdapter(factory, AzureImageFixture.Options()).StreamAsync(request, TestContext.Current.CancellationToken)) { }
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>缺失/重复/多余终态、DONE、无效尾部和跨操作事件均不能先交付任何完成项。</summary>
    [Theory]
    [InlineData(false, "missing")] [InlineData(true, "missing")] [InlineData(false, "duplicate")] [InlineData(true, "duplicate")]
    [InlineData(false, "late")] [InlineData(true, "late")] [InlineData(false, "done-only")] [InlineData(true, "done-only")]
    [InlineData(false, "early-done")] [InlineData(true, "early-done")] [InlineData(false, "duplicate-done")] [InlineData(true, "duplicate-done")]
    [InlineData(false, "data-after-done")] [InlineData(true, "data-after-done")] [InlineData(false, "truncated")] [InlineData(true, "truncated")]
    [InlineData(false, "wrong-family")] [InlineData(true, "wrong-family")] [InlineData(false, "count-missing")] [InlineData(true, "count-missing")]
    public async Task InvalidLifecycle_NeverExposesFinal(bool editing, string kind)
    {
        var frames = AzureImageStreamFixture.Case(editing).GetProperty("frames");
        var first = AzureImageStreamFixture.Frame(frames[1].GetProperty("data")); var final = AzureImageStreamFixture.Frame(frames[2].GetProperty("data"));
        var wrong = JsonNode.Parse(frames[2].GetProperty("raw_data").GetString()!)!.AsObject(); wrong["type"] = editing ? "image_generation.completed" : "image_edit.completed";
        var wire = kind switch
        {
            "missing" => first, "duplicate" => first + final + final, "late" => first + final + first, "done-only" => "data: [DONE]\n\n",
            "early-done" => first + "data: [DONE]\n\n", "duplicate-done" => final + "data: [DONE]\n\ndata: [DONE]\n\n",
            "data-after-done" => final + "data: [DONE]\n\n" + first, "truncated" => first + final + "data: {",
            "wrong-family" => first + "data: " + wrong.ToJsonString() + "\n\n" + final, _ => first + final
        };
        using var one = new AzureImageReadStream([1]); using var two = new AzureImageReadStream([2]); using var mask = new AzureImageReadStream([3]);
        using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var item in AzureImageStreamFixture.Read(client, editing, kind == "count-missing" ? 2 : 1, kind == "count-missing" ? [one, two] : [one], kind == "count-missing" ? mask : null, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Data.GetProperty("type").GetString()!.EndsWith(".completed", StringComparison.Ordinal));
        Assert.DoesNotContain(seen, item => item.Completed == true); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals);
        Assert.False(one.Disposed); Assert.False(two.Disposed); Assert.False(mask.Disposed);
    }

    /// <summary>完整事件仍必须有明确原生类型/图片与正确字段结构，未知 JSON 也不能有重复或坏 Unicode。</summary>
    [Theory]
    [InlineData(false, "empty-image")] [InlineData(true, "empty-image")] [InlineData(false, "bad-index")] [InlineData(true, "bad-index")]
    [InlineData(false, "near-index")] [InlineData(true, "near-index")] [InlineData(false, "bad-usage")] [InlineData(true, "bad-usage")]
    [InlineData(false, "fraction-usage")] [InlineData(true, "fraction-usage")] [InlineData(false, "bad-type")] [InlineData(true, "bad-type")]
    [InlineData(false, "event-conflict")] [InlineData(true, "event-conflict")] [InlineData(false, "duplicate-json")] [InlineData(true, "duplicate-json")]
    [InlineData(false, "unicode")] [InlineData(true, "unicode")]
    public async Task InvalidNativeFrame_IsRejectedBeforeSuccess(bool editing, string kind)
    {
        var partial = kind is "bad-index" or "near-index"; var fixture = AzureImageStreamFixture.Case(editing).GetProperty("frames")[partial ? 1 : 2];
        var data = JsonNode.Parse(fixture.GetProperty("raw_data").GetString()!)!.AsObject();
        if (kind == "empty-image") data["b64_json"] = "";
        if (kind == "bad-index") data["partial_image_index"] = -1;
        if (kind == "near-index") data["partial_image_index"] = JsonNode.Parse("1.00000000000000000000000000001");
        if (kind == "bad-usage") data["usage"] = new JsonArray();
        if (kind == "fraction-usage") data["usage"]!["input_tokens"] = JsonNode.Parse("0.00000000000000000000000000001");
        if (kind == "bad-type") data["type"] = 200;
        var raw = data.ToJsonString(); if (kind == "duplicate-json") raw = raw[..^1] + ",\"future-duplicate\":{\"x\":1,\"x\":2}}";
        if (kind == "unicode") raw = raw[..^1] + ",\"future-bad\":\"\\ud800\"}";
        var wire = (kind == "event-conflict" ? "event: other\n" : "") + "data: " + raw + "\n\n";
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Empty(seen); Assert.Equal(1, factory.Calls);
    }

    /// <summary>整数指数和大计数保持原字面值，时间不被重写为整数，索引不作为图片关联 ID。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeNumericSpelling_IsNotRoundedOrNormalized(bool editing)
    {
        var frames = AzureImageStreamFixture.Case(editing).GetProperty("frames");
        var partial = frames[1].GetProperty("raw_data").GetString()!.Replace("\"partial_image_index\":0", "\"partial_image_index\":1e999999999999999999999999", StringComparison.Ordinal);
        var final = frames[2].GetProperty("raw_data").GetString()!.Replace("\"created_at\":9007199254740993", "\"created_at\":0.125", StringComparison.Ordinal);
        var wire = "data: " + partial + "\n\ndata: " + final + "\n\n";
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Equal(partial, seen[0].Data.GetRawText()); Assert.Equal(final, seen[1].Data.GetRawText()); Assert.Equal("0.125", seen[1].Data.GetProperty("created_at").GetRawText());
    }
}
