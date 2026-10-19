using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>转换包仅测试时引用；Azure 生产包不依赖转换或 OpenAI 包。</summary>
public sealed class ConversionTests
{
    /// <summary>显式 Responses 到 Azure Chat 使用固定上游认证，结果保留原始 JSON 和转换诊断。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitConversion_UsesSelectedNativeAuthentication(bool external)
    {
        const string reply = """{"id":"native-chat","object":"chat.completion","created":1,"model":"native","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""";
        var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var expected = JsonNode.Parse("""{"model":"deploy.1","stream":false,"messages":[{"role":"user","content":[{"type":"text","text":"hello"}]}]}""");
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))));
            if (external) Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); else Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("api-key")));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        });
        var native = new AzureLlmProvider(new AzureHttpFactory(handler), tokenSource: source, timeProvider: clock); var converted = new ConvertingLlmProvider(native, LlmProtocol.ChatCompletions, "explicit");
        var result = await converted.CreateAsync(JsonNode.Parse("""{"model":"public","input":"hello"}""")!.AsObject(), AzureFixture.Context(false, external: external), TestContext.Current.CancellationToken);
        Assert.Equal("done", result.Data.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString()); Assert.Empty(result.Metadata.Conversion!.Diagnostics);
        Assert.Equal("native-chat", result.Metadata.Conversion.UpstreamData.GetProperty("id").GetString()); Assert.Equal(external ? 1 : 0, source.Calls); Assert.Equal(1, handler.Calls);
    }
}
