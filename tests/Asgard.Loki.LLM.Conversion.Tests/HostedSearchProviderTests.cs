using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过公共 SDK 和原生 HTTP/SSE 验证搜索配置；返回纯文本夹具不宣称真实搜索效果。</summary>
public sealed class HostedSearchProviderTests
{
    /// <summary>六个方向分别执行完整/流式的实际 SDK 流水线。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Calls
    {
        get { TheoryData<LlmProtocol, LlmProtocol, bool> data = []; foreach (var from in Protocols) foreach (var to in Protocols) if (from != to) { data.Add(from, to, false); data.Add(from, to, true); } return data; }
    }
    /// <summary>核对完整最终请求及原生工具类型，未搜索的合法回复仍可正常转换并记录元数据。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicPipeline_VerifiesConfigurationNotLiveSearch(LlmProtocol source, LlmProtocol target, bool stream)
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var wire = CustomToolFixtures.Parse(await request.Content!.ReadAsStringAsync(token)); var expected = HostedSearchFixtures.Expected(target);
            if (target == LlmProtocol.Gemini) expected.Remove("model"); else expected["stream"] = stream;
            CustomToolConversionTests.Equal(expected, wire); Assert.DoesNotContain("function", wire.ToJsonString());
            if (!stream) return Json(HostedSearchFixtures.TextResponse(target));
            var body = string.Concat(HostedSearchFixtures.TextStream(target).Select(e => "data: " + e.ToJsonString() + "\n\n"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(body)) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
        });
        await using var client = ConvertingProviderTests.Build(handler, source, target, options: HostedSearchFixtures.Options(target));
        var request = HostedSearchFixtures.Request(source); request["model"] = "public"; var before = request.ToJsonString();
        if (stream)
        {
            var events = await ConvertingProviderTests.StreamAsync(client, source, request); Assert.NotEmpty(events);
            Assert.Contains(events, e => e.Data.GetRawText().Contains("done", StringComparison.Ordinal)); Assert.All(events, e => Assert.Empty(e.Metadata.Conversion!.Diagnostics));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, source, request); Assert.Contains("done", result.Data.GetRawText()); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
            CustomToolConversionTests.Equal(HostedSearchFixtures.TextResponse(target), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()));
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>不可表达的域名范围或离线权限在两种入口都必须先于任何网络请求失败。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Constraints_FailBeforeHttp(bool stream, bool offline)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.Responses, LlmProtocol.Gemini, options: HostedSearchFixtures.Options(LlmProtocol.Gemini) with { AllowLoss = true });
        var request = HostedSearchFixtures.Request(LlmProtocol.Responses); request["model"] = "public";
        if (offline) request["tools"]![0]!["external_web_access"] = false;
        else request["tools"]![0]!["filters"] = CustomToolFixtures.Parse("""{"allowed_domains":["example.com"]}""");
        if (stream) await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, LlmProtocol.Responses, request));
        else await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.Responses, request));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>实际原生适配器返回加密搜索内容时，即使普通损耗获准，装饰器仍拒绝误报可回放结果。</summary>
    [Theory] [InlineData(LlmProtocol.Responses)] [InlineData(LlmProtocol.Gemini)]
    public async Task EncryptedSearchReply_FailsWithoutRetry(LlmProtocol source)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(HostedSearchBoundaryTests.EncryptedResponse(false))));
        await using var client = ConvertingProviderTests.Build(handler, source, LlmProtocol.ClaudeMessages, options: HostedSearchFixtures.Options(LlmProtocol.ClaudeMessages) with { AllowLoss = true });
        var request = HostedSearchFixtures.Request(source); request["model"] = "public";
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, source, request));
        Assert.Equal("opaque_state", Assert.Single(error.Diagnostics).Code); Assert.Equal(1, handler.Calls);
    }
    private static LlmProtocol[] Protocols => [LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];
    private static HttpResponseMessage Json(JsonObject value) => new(HttpStatusCode.OK) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
}
