using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过实际公共 SDK 和原生 HTTP 适配链核对 Chat 搜索请求及未改变的回复生命周期。</summary>
public sealed class ChatSearchProviderTests
{
    /// <summary>四个方向分别验证完整 JSON 与分片 SSE 回复模式。</summary>
    public static IEnumerable<object[]> Calls()
    {
        foreach (var direction in ChatSearchFixtures.Directions())
            foreach (var stream in new[] { false, true }) yield return [direction[0], direction[1], stream];
    }

    /// <summary>核对独立最终请求、正确原生认证和一次发送；替代文本回复不宣称真实搜索执行。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicPipeline_PreservesSearchConfiguration(LlmProtocol from, LlmProtocol to, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = ChatSearchFixtures.Read(to, true); expected["stream"] = stream;
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            if (to == LlmProtocol.ClaudeMessages) Assert.Equal("bound-key", request.Headers.GetValues("x-api-key").Single());
            else Assert.Equal("bound-key", request.Headers.Authorization!.Parameter);
            if (!stream) return Json(HostedSearchFixtures.TextResponse(to));
            var body = string.Concat(InputFileFixtures.TextStream(to).Select(e => "data: " + e.ToJsonString() + "\n\n"));
            if (to == LlmProtocol.ChatCompletions) body += "data: [DONE]\n\n";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) };
            response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        await using var client = ConvertingProviderTests.Build(handler, from, to, options: ChatSearchFixtures.Options(to));
        var source = ChatSearchFixtures.Read(from); source["model"] = "public";
        if (stream)
        {
            var events = await ConvertingProviderTests.StreamAsync(client, from, source);
            Assert.All(events, e => Assert.Empty(e.Metadata.Conversion!.Diagnostics));
            var last = events.Last().Data;
            Assert.Equal(from switch { LlmProtocol.ChatCompletions => "stop", LlmProtocol.Responses => "response.completed", _ => "message_stop" },
                from == LlmProtocol.ChatCompletions ? last.GetProperty("choices")[0].GetProperty("finish_reason").GetString() : last.GetProperty("type").GetString());
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, from, source);
            Assert.Empty(result.Metadata.Conversion!.Diagnostics); Assert.Contains("done", result.Data.GetRawText());
            CustomToolConversionTests.Equal(HostedSearchFixtures.TextResponse(to), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>未知的 Chat 搜索/函数组合在 JSON 与 SSE 模式均于发送前失败，不借用旧组合开关放行。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task FunctionCombination_FailsBeforeHttp(LlmProtocol from, LlmProtocol to, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("未验证组合不能发起 HTTP。"));
        await using var client = ConvertingProviderTests.Build(handler, from, to,
            options: ChatSearchFixtures.Options(to) with { AllowLoss = true, AllowHostedSearchWithFunctionTools = true });
        var source = ChatSearchFixtures.Read(from); source["model"] = "public";
        var tools = source["tools"] as JsonArray;
        if (tools is null) source["tools"] = tools = new JsonArray();
        tools.Add(ConversionFixtures.Request(from)["tools"]![0]!.DeepClone());
        LlmConversionException error = stream
            ? await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, from, source))
            : await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, from, source));
        Assert.Equal("chat_search_function_combination_not_supported", Assert.Single(error.Diagnostics).Code); Assert.Equal(0, handler.Calls);
    }

    /// <summary>源默认/auto 搜索不得转为 Chat 必需搜索，两种回复模式都须在任何 HTTP 前失败。</summary>
    [Theory] [InlineData(LlmProtocol.Responses, false)] [InlineData(LlmProtocol.Responses, true)]
    [InlineData(LlmProtocol.ClaudeMessages, false)] [InlineData(LlmProtocol.ClaudeMessages, true)]
    public async Task TriggerMismatch_FailsBeforeHttp(LlmProtocol from, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("语义不匹配不能发起 HTTP。"));
        await using var client = ConvertingProviderTests.Build(handler, from, LlmProtocol.ChatCompletions,
            options: ChatSearchFixtures.Options(LlmProtocol.ChatCompletions) with { AllowLoss = true });
        var source = ChatSearchFixtures.Read(from); source["model"] = "public"; source.Remove("tool_choice");
        LlmConversionException error = stream
            ? await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, from, source))
            : await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, from, source));
        Assert.Equal("hosted_search_choice_not_representable", Assert.Single(error.Diagnostics).Code); Assert.Equal(0, handler.Calls);
    }

    /// <summary>被明确许可的上下文大小指导值损耗会进入公共元数据，搜索要求仍保留。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ContextLoss_IsVisibleInSdkMetadata(bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = ChatSearchFixtures.Read(LlmProtocol.ClaudeMessages, true); expected["stream"] = stream;
            CustomToolConversionTests.Equal(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)));
            if (!stream) return Json(HostedSearchFixtures.TextResponse(LlmProtocol.ClaudeMessages));
            var body = string.Concat(InputFileFixtures.TextStream(LlmProtocol.ClaudeMessages).Select(e => "data: " + e.ToJsonString() + "\n\n"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) };
            response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.ClaudeMessages,
            options: ChatSearchFixtures.Options(LlmProtocol.ClaudeMessages) with { AllowLoss = true });
        var source = ChatSearchFixtures.Read(LlmProtocol.ChatCompletions); source["model"] = "public"; source["web_search_options"]!["search_context_size"] = "high";
        if (stream)
        {
            var events = await ConvertingProviderTests.StreamAsync(client, LlmProtocol.ChatCompletions, source);
            Assert.Contains(events.Last().Metadata.Conversion!.Diagnostics, d => d.Code == "search_context_size_not_representable");
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, source);
            Assert.Equal("search_context_size_not_representable", Assert.Single(result.Metadata.Conversion!.Diagnostics).Code);
        }
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>离线替代 JSON 响应。</summary>
    private static HttpResponseMessage Json(JsonObject value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
}
