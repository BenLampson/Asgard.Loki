using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>富工具结果通过公共 SDK 的最终 HTTP 报文和 SSE 回复验证；SDK 不执行其中的工具。</summary>
public sealed class RichToolResultProviderTests
{
    /// <summary>八种结果映射分别覆盖完整和流式回复。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool, bool> Calls
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol, bool, bool> data = [];
            foreach (var from in RichToolResultFixtures.TextProtocols) foreach (var to in RichToolResultFixtures.TextProtocols)
                if (from != to) { data.Add(from, to, false, false); data.Add(from, to, false, true); }
            foreach (var from in new[] { LlmProtocol.Responses, LlmProtocol.ClaudeMessages })
                foreach (var stream in new[] { false, true }) data.Add(from, from == LlmProtocol.Responses ? LlmProtocol.ClaudeMessages : LlmProtocol.Responses, true, stream);
            return data;
        }
    }

    /// <summary>逐项比较完整原生请求，工具结果仍在原关联项内，媒体未变成普通用户消息。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicPipeline_PreservesRichRequestAndReplyMetadata(LlmProtocol source, LlmProtocol target, bool media, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("wire.invalid", request.RequestUri!.Host); var expected = RichToolResultFixtures.Read(target, media, true); expected["stream"] = stream;
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            return Reply(target, stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, source, target); var request = RichToolResultFixtures.Read(source, media); request["model"] = "public";
        var before = request.ToJsonString();
        if (stream)
        {
            var result = await ConvertingProviderTests.StreamAsync(client, source, request); Assert.NotEmpty(result);
            Assert.All(result, e => Assert.Empty(e.Metadata.Conversion!.Diagnostics)); Assert.Contains(result, e => e.Data.GetRawText().Contains("done", StringComparison.Ordinal));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, source, request); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
            CustomToolConversionTests.Equal(HostedSearchFixtures.TextResponse(target), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>custom 文本结果数组和 Claude 媒体结果数组均使用原请求的可逆身份。</summary>
    public static TheoryData<LlmProtocol, bool, bool> CustomCalls
    {
        get
        {
            TheoryData<LlmProtocol, bool, bool> data = [];
            foreach (var stream in new[] { false, true })
            { data.Add(LlmProtocol.ChatCompletions, false, stream); data.Add(LlmProtocol.ClaudeMessages, false, stream); data.Add(LlmProtocol.ClaudeMessages, true, stream); }
            return data;
        }
    }

    /// <summary>最终请求的别名、ID、输入包装和结果内容都由独立金样核对，原生回复单独保留。</summary>
    [Theory] [MemberData(nameof(CustomCalls))]
    public async Task CustomRichHistory_UsesPublicPipeline(LlmProtocol target, bool media, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token)); var alias = CustomToolFixtures.Name(wire, target);
            var expected = RichCustomToolResultTests.Expected(target, alias, media); expected["stream"] = stream; CustomToolConversionTests.Equal(expected, wire);
            return Reply(target, stream);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, target, options: CustomToolFixtures.Options);
        var request = RichCustomToolResultTests.Request(media); request["model"] = "public";
        if (stream)
        {
            var result = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, request); Assert.NotEmpty(result);
            Assert.All(result, e => Assert.Empty(e.Metadata.Conversion!.Diagnostics));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, request); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
            CustomToolConversionTests.Equal(HostedSearchFixtures.TextResponse(target), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>错误标记和没有原生对应结构的内容在完整/流式入口都先于 HTTP 失败。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, int, bool> Failures
    {
        get
        {
            TheoryData<LlmProtocol, LlmProtocol, int, bool> data = [];
            foreach (var stream in new[] { false, true })
            {
                foreach (var target in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.Gemini }) data.Add(LlmProtocol.ClaudeMessages, target, 0, stream);
                foreach (var source in RichToolResultFixtures.TextProtocols) data.Add(source, LlmProtocol.Gemini, 1, stream);
                foreach (var source in new[] { LlmProtocol.Responses, LlmProtocol.ClaudeMessages }) data.Add(source, LlmProtocol.ChatCompletions, 2, stream);
                data.Add(LlmProtocol.Responses, LlmProtocol.Gemini, 3, stream);
            }
            return data;
        }
    }

    /// <summary>普通损耗许可不执行错误降级、媒体序列化、Gemini 包装或网络重试。</summary>
    [Theory] [MemberData(nameof(Failures))]
    public async Task UnsupportedResult_FailsWithoutAnyHttp(LlmProtocol source, LlmProtocol target, int scenario, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: CustomToolFixtures.Options with { AllowLoss = true });
        var request = scenario == 3 ? RichCustomToolResultTests.Request() : RichToolResultFixtures.Read(source, scenario == 2);
        if (scenario == 0) RichToolResultFixtures.Result(request, source)["is_error"] = true;
        request["model"] = "public";
        var error = stream ? await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, source, request))
            : await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, source, request));
        Assert.Contains(error.Diagnostics, d => d.Code == (scenario == 0 ? "tool_result_error_not_representable" : scenario == 3 ? "custom_tool_result_content_not_supported" : "rich_tool_result_not_representable"));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>普通文本回复只验证调用接线，不执行工具或证明模型读取了媒体。</summary>
    private static HttpResponseMessage Reply(LlmProtocol target, bool stream)
    {
        if (!stream) return new(HttpStatusCode.OK) { Content = new StringContent(HostedSearchFixtures.TextResponse(target).ToJsonString(), Encoding.UTF8, "application/json") };
        var body = string.Concat(InputFileFixtures.TextStream(target).Select(e => "data: " + e.ToJsonString() + "\n\n"));
        if (target == LlmProtocol.ChatCompletions) body += "data: [DONE]\n\n";
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) };
        response.Content.Headers.ContentType = new("text/event-stream"); return response;
    }
}
