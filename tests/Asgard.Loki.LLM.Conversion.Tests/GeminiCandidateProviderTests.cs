using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>通过公共 SDK、固定 Google 上游和替代 HTTP 验证完整候选输出。</summary>
public sealed class GeminiCandidateProviderTests
{
    /// <summary>完整请求与结果快照来自独立样本，所有候选保留在同一次响应和一次调用中。</summary>
    [Theory]
    [InlineData("text-two")]
    [InlineData("tools-isolated")]
    [InlineData("citations-isolated")]
    [InlineData("single-nonzero")]
    [InlineData("empty-and-filtered")]
    public async Task Complete_PublicPipelineMatchesGolden(string name)
    {
        var sample = GeminiCandidateFixtures.Case(name); var raw = sample["source"]!.AsObject();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://wire.invalid/v1/models/model-a:generateContent", request.RequestUri!.AbsoluteUri);
            Assert.Equal("bound-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            var expected = JsonNode.Parse("""{"contents":[{"role":"user","parts":[{"text":"hello"}]}],"generationConfig":{"maxOutputTokens":32}}""")!;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
            return Reply(raw);
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini,
            options: GeminiCandidateFixtures.Options());
        var request = Request(); var before = request.ToJsonString();
        var result = await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, request);
        GeminiCandidateFixtures.Equal(sample["target"]!, result.Data);
        var conversion = Assert.IsType<LlmConversionInfo>(result.Metadata.Conversion);
        GeminiCandidateFixtures.Equal(raw, conversion.UpstreamData); Assert.Empty(conversion.Diagnostics);
        Assert.Equal("public", result.Metadata.PublicModel); Assert.Equal("model-a", result.Metadata.UpstreamModel);
        Assert.Equal(1, handler.Calls); Assert.Equal(before, request.ToJsonString());
    }

    /// <summary>后续候选出错导致整次调用失败，不自动重试或返回前面的成功候选。</summary>
    [Theory]
    [InlineData("duplicate-index")]
    [InlineData("opaque")]
    [InlineData("unknown")]
    public async Task InvalidLaterCandidate_FailsWithoutRetry(string kind)
    {
        var raw = GeminiCandidateFixtures.Case("text-two")["source"]!.AsObject();
        if (kind == "duplicate-index") raw["candidates"]![1]!["index"] = 7;
        else if (kind == "opaque") raw["candidates"]![1]!["content"]!["parts"]![0]!["thoughtSignature"] = "opaque";
        else raw["candidates"]![1]!["future"] = null;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(raw)));
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini,
            options: GeminiCandidateFixtures.Options(kind != "unknown"));
        if (kind == "duplicate-index")
            await Assert.ThrowsAsync<InvalidDataException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, Request()));
        else await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, Request()));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>直接上下文转换与公共装饰器使用同一候选规则，不丢失请求上下文或上游原文。</summary>
    [Fact]
    public void RequestContext_UsesCompleteCandidateMapping()
    {
        var source = GeminiCandidateFixtures.Case("tools-isolated");
        var request = LlmProtocolConverter.ConvertRequest(Request(), LlmProtocol.ChatCompletions, LlmProtocol.Gemini, GeminiCandidateFixtures.Options());
        var result = LlmProtocolConverter.ConvertResponse(source["source"]!.AsObject(), request.Context!);
        GeminiCandidateFixtures.Equal(source["target"]!, result.Data); Assert.Same(request.Context, result.Context);
        GeminiCandidateFixtures.Equal(source["source"]!, result.OriginalData);
    }

    /// <summary>本轮没有添加候选数量请求控制，仍明确拒绝未实现的 n 设置。</summary>
    [Fact]
    public async Task UnsupportedRequestCount_FailsBeforeHttp()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.Gemini,
            options: GeminiCandidateFixtures.Options());
        var request = Request(); request["n"] = 2;
        await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, request));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>普通原生上游响应，不模拟模型推理或真实云账号。</summary>
    private static HttpResponseMessage Reply(JsonObject raw) => new(HttpStatusCode.OK)
    { Content = new StringContent(raw.ToJsonString(), Encoding.UTF8, "application/json") };
    /// <summary>只使用已实现的公共请求子集，避免假称已支持多候选请求控制。</summary>
    private static JsonObject Request() => JsonNode.Parse("""{"model":"public","messages":[{"role":"user","content":"hello"}],"max_completion_tokens":32}""")!.AsObject();
}
