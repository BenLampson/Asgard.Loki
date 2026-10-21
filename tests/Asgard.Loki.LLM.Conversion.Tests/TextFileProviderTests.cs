using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>公开 SDK 及原生适配器上的纯文本文件协议验证，全部使用离线 HTTP 替身。</summary>
public sealed class TextFileProviderTests
{
    /// <summary>六个原生转换方向分别使用完整与流式回复。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Calls
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol, bool> data = [];
            foreach (var source in TextFileFixtures.Protocols) foreach (var target in TextFileFixtures.Protocols)
                if (source != target) foreach (var stream in new[] { false, true }) data.Add(source, target, stream); return data;
        }
    }
    /// <summary>比较完整独立原生请求、实际模型、路径和认证，不仅检查发送了某个参数。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicSdk_MatchesCompleteNativeRequest(LlmProtocol source, LlmProtocol target, bool stream)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var expected = TextFileFixtures.Read(target);
            if (target == LlmProtocol.Gemini) expected.Remove("model"); else expected["stream"] = stream;
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)));
            Assert.Equal(target switch
            {
                LlmProtocol.Responses => "https://wire.invalid/v1/responses",
                LlmProtocol.ClaudeMessages => "https://wire.invalid/v1/messages",
                _ => "https://wire.invalid/v1/models/model-a:" + (stream ? "streamGenerateContent?alt=sse" : "generateContent")
            }, message.RequestUri!.AbsoluteUri);
            if (target == LlmProtocol.Responses) Assert.Equal("Bearer bound-key", message.Headers.Authorization!.ToString());
            else Assert.Equal("bound-key", Assert.Single(message.Headers.GetValues(target == LlmProtocol.Gemini ? "x-goog-api-key" : "x-api-key")));
            return Reply(target, stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: TextFileFixtures.Options);
        var request = TextFileFixtures.Read(source); request["model"] = "public"; var before = request.ToJsonString();
        if (stream)
        {
            var result = await ConvertingProviderTests.StreamAsync(client, source, request); Assert.NotEmpty(result);
            Assert.All(result, item => { Assert.Empty(item.Metadata.Conversion!.Diagnostics); Assert.Equal(target, item.Metadata.Conversion.UpstreamProtocol); Assert.Equal(source, item.Metadata.Conversion.PublicProtocol); });
            CustomToolConversionTests.Equal(InputFileFixtures.TextStream(target)[^1], JsonNode.Parse(result[^1].Metadata.Conversion!.UpstreamData.GetRawText()));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, source, request); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
            CustomToolConversionTests.Equal(HostedSearchFixtures.TextResponse(target), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>三个文件源和两个模式分别验证编码/大小/目标文档边界。</summary>
    public static TheoryData<LlmProtocol, bool, string> Rejections
    {
        get
        {
            TheoryData<LlmProtocol, bool, string> data = [];
            foreach (var source in TextFileFixtures.Protocols) foreach (var stream in new[] { false, true })
                foreach (var kind in new[] { "encoding", "size", "chat" }) data.Add(source, stream, kind); return data;
        }
    }
    /// <summary>严格文件边界在 HTTP 之前失败，即使已同意普通元数据损耗也不重试/降级。</summary>
    [Theory] [MemberData(nameof(Rejections))]
    public async Task PublicSdk_RejectsBeforeHttp(LlmProtocol source, bool stream, string kind)
    {
        var target = kind == "chat" ? LlmProtocol.ChatCompletions : source == LlmProtocol.Responses ? LlmProtocol.ClaudeMessages : LlmProtocol.Responses;
        var options = TextFileFixtures.Options with { AllowLoss = true };
        if (kind == "encoding") options = options with { TextFileEncoding = LlmTextFileEncoding.Unspecified };
        if (kind == "size") options = options with { MaxTextFileBytes = 18 };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: options);
        var request = TextFileFixtures.Read(source); request["model"] = "public";
        var error = stream
            ? await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, source, request))
            : await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, source, request));
        Assert.Contains(error.Diagnostics, d => d.Code == (kind == "encoding" ? "text_file_encoding_required" : kind == "size" ? "text_file_byte_limit" : "text_file_target_not_representable"));
        Assert.Equal(0, handler.Calls);
    }
    /// <summary>Base64 正确但 UTF-8 不合法也不能经完整或 SSE 模式发给模型。</summary>
    [Theory] [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)] [InlineData(LlmProtocol.Gemini, false)] [InlineData(LlmProtocol.Gemini, true)]
    public async Task PublicSdk_InvalidUtf8DoesNotDispatch(LlmProtocol source, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, source, LlmProtocol.ClaudeMessages, options: TextFileFixtures.Options with { AllowLoss = true });
        var request = TextFileFixtures.Read(source); request["model"] = "public"; TextFileFixtures.Data(request, source, "/w==");
        if (stream) await Assert.ThrowsAsync<InvalidDataException>(() => ConvertingProviderTests.StreamAsync(client, source, request));
        else await Assert.ThrowsAsync<InvalidDataException>(() => ConvertingProviderTests.CompleteAsync(client, source, request)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>普通文本响应仅用于协议接线测试，不证明真实模型能正确理解文档。</summary>
    private static HttpResponseMessage Reply(LlmProtocol target, bool stream)
    {
        if (!stream) return new(HttpStatusCode.OK) { Content = new StringContent(HostedSearchFixtures.TextResponse(target).ToJsonString(), Encoding.UTF8, "application/json") };
        var body = string.Concat(InputFileFixtures.TextStream(target).Select(frame => "data: " + frame.ToJsonString() + "\n\n"));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
    }
}
