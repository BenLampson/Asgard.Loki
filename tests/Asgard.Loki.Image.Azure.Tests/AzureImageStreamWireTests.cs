using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Image.Azure.Tests;

/// <summary>完整独立原生流向量、SSE 身份与本次调用的完成边界。</summary>
public sealed class AzureImageStreamWireTests
{
    /// <summary>原生一/多图事件保持原始顺序，首个完成起所有尾部仅在传输释放后公开。</summary>
    [Theory] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(true, 1)] [InlineData(true, 2)]
    public async Task CompleteNativeWire_PreservesAllFieldsOrderAndOwnership(bool editing, int count)
    {
        var sourceCase = AzureImageStreamFixture.Case(editing, count); var expected = sourceCase.GetProperty("frames");
        using var source = new AzureImageReadStream(Encoding.UTF8.GetBytes(AzureImageStreamFixture.Wire(editing, count)));
        var form = AzureImageStreamFixture.Data().GetProperty("edit_requests")[count - 1];
        var images = form.GetProperty("images").EnumerateArray().Select(item => new AzureImageReadStream(Convert.FromHexString(item.GetProperty("hex").GetString()!))).ToArray();
        using var mask = count == 2 ? new AzureImageReadStream(Convert.FromHexString(form.GetProperty("mask").GetProperty("hex").GetString()!)) : null;
        try
        {
            using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
            {
                Assert.Equal("text/event-stream", Assert.Single(message.Headers.Accept).MediaType); Assert.Null(message.Headers.Authorization);
                Assert.Equal(new[] { "fixture-azure-image-key" }, message.Headers.GetValues("api-key"));
                Assert.Equal("https://azure-image.example.invalid/prefix/openai/deployments/literal.image-deployment/images/" + (editing ? "edits" : "generations") + "?api-version=fixture-version%2Bliteral", message.RequestUri!.AbsoluteUri);
                if (editing) Assert.Null(message.Content!.Headers.ContentLength);
                var bytes = await message.Content!.ReadAsByteArrayAsync(token);
                if (editing)
                {
                    var boundary = message.Content.Headers.ContentType!.Parameters.Single(item => item.Name == "boundary").Value!.Trim('"');
                    var normalized = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace(boundary, "<BOUNDARY>", StringComparison.Ordinal));
                    Assert.Equal(AzureImageFixture.Bytes(form.GetProperty("golden").GetString()!), normalized);
                }
                else
                {
                    using var document = JsonDocument.Parse(bytes);
                    Assert.True(JsonElement.DeepEquals(AzureImageStreamFixture.Data().GetProperty("generation_requests")[count - 1], document.RootElement));
                }
                return AzureImageStreamFixture.Response(source);
            } };
            await using var client = AzureImageFixture.Build(factory, editing, AzureImageFixture.Options(bodyModel: editing && count == 2 ? "native-body-model" : null));
            var seen = new List<AzureImageStreamEvent>(); var completionSeen = false;
            await foreach (var item in AzureImageStreamFixture.Read(client, editing, count, images, mask, TestContext.Current.CancellationToken))
            {
                var index = seen.Count; var native = expected[index];
                if (item.Data.GetProperty("type").GetString()!.EndsWith(".completed", StringComparison.Ordinal)) completionSeen = true;
                if (completionSeen) { Assert.True(source.Eof); Assert.True(source.Disposed); Assert.Equal(1, factory.ClientDisposals); }
                else Assert.False(source.Eof);
                Assert.Equal(native.GetProperty("raw_data").GetString(), item.Data.GetRawText());
                Assert.Equal(native.GetProperty("event").GetString(), item.EventName); Assert.Equal(native.GetProperty("id").GetString(), item.EventId);
                Assert.Equal(native.GetProperty("retry").GetInt64(), item.RetryMilliseconds);
                Assert.Equal(new ImageResultMetadata("images", "literal.image-deployment", "azure-test"), item.Metadata);
                if (editing) Assert.Equal(index == expected.GetArrayLength() - 1, item.Completed); else Assert.Null(item.Completed);
                seen.Add(item);
            }
            Assert.Equal(expected.GetArrayLength(), seen.Count); Assert.Equal(count, seen.Count(item => item.Data.GetProperty("type").GetString()!.EndsWith(".completed", StringComparison.Ordinal)));
            Assert.Equal(editing ? 1 : 0, seen.Count(item => item.Completed == true)); Assert.False(factory.Disposed);
            Assert.All(images, image => Assert.False(image.Disposed)); Assert.True(mask is null || !mask.Disposed); Assert.Equal(1, factory.Calls);
        }
        finally { foreach (var image in images) image.Dispose(); }
    }

    /// <summary>请求省略 n/partial_images 不填默认值，显式流入口只在 stream 缺失时增加 true。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OmittedNativeSettings_AreNotInjectedExceptExplicitStream(bool editing)
    {
        using var image = new AzureImageReadStream([1]); var generation = AzureImageStreamFixture.Generation();
        generation.Parameters.Remove("n"); generation.Parameters.Remove("partial_images"); generation.Parameters.Remove("stream");
        var edit = AzureImageStreamFixture.Edit(1, [image]) with { Fields = [new("Future", "literal")] };
        using var factory = new AzureImageTestFactory { OnSend = async (message, token) =>
        {
            var raw = Encoding.UTF8.GetString(await message.Content!.ReadAsByteArrayAsync(token));
            if (editing) { Assert.DoesNotContain("name=n", raw); Assert.DoesNotContain("partial_images", raw); Assert.Contains("name=stream\r\n\r\ntrue", raw); }
            else
            {
                using var document = JsonDocument.Parse(raw); Assert.False(document.RootElement.TryGetProperty("n", out _));
                Assert.False(document.RootElement.TryGetProperty("partial_images", out _)); Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
            }
            return AzureImageStreamFixture.Response(AzureImageStreamFixture.Wire(editing));
        } };
        if (editing) await foreach (var _ in new AzureImageEditAdapter(factory, AzureImageFixture.Options()).StreamMultipartEditAsync(edit, TestContext.Current.CancellationToken)) { }
        else await foreach (var _ in new AzureImageAdapter(factory, AzureImageFixture.Options()).StreamAsync(generation, TestContext.Current.CancellationToken)) { }
        Assert.Equal(1, factory.Calls); Assert.False(image.Disposed);
    }

    /// <summary>部分图数量不是承诺，零个部分图或少于请求的部分图仍保留原生成功完成项。</summary>
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task PartialImageCount_IsNotAssumed(bool editing, bool none)
    {
        var frames = AzureImageStreamFixture.Case(editing).GetProperty("frames");
        var wire = string.Concat(frames.EnumerateArray().Where(item => !none || !item.GetProperty("data").GetProperty("type").GetString()!.EndsWith(".partial_image", StringComparison.Ordinal))
            .Select(item => AzureImageStreamFixture.Frame(item.GetProperty("data"), item.GetProperty("event").GetString())));
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Equal(none ? 2 : 3, seen.Count); Assert.EndsWith(".completed", seen[^1].Data.GetProperty("type").GetString());
    }

    /// <summary>可选 DONE 只在真实图片完成项之后作为传输控制接受，不构造 JSON 事件。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OptionalDone_AfterNativeCompletionDoesNotCreateEvent(bool editing)
    {
        var wire = AzureImageStreamFixture.Wire(editing) + "data: [DONE]\r\n\r\n: final heartbeat\r\n\r\n";
        using var image = new AzureImageReadStream([1]); using var factory = new AzureImageTestFactory { OnSend = (_, _) => Task.FromResult(AzureImageStreamFixture.Response(wire)) };
        await using var client = AzureImageFixture.Build(factory, editing); var seen = new List<AzureImageStreamEvent>();
        await foreach (var item in AzureImageStreamFixture.Read(client, editing, 1, [image], null, TestContext.Current.CancellationToken)) seen.Add(item);
        Assert.Equal(3, seen.Count); Assert.EndsWith(".completed", seen[^1].Data.GetProperty("type").GetString()); Assert.Equal(1, factory.Calls);
    }
}
