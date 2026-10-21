using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>Azure 原生地址、认证和完整报文由独立金样验证，不复用供应商转换输出生成预期。</summary>
public sealed class NativeJsonTests
{
    /// <summary>v1/旧式、两协议和两认证方式分别比较最终 URL、完整请求和原始结果。</summary>
    [Theory] [InlineData(AzureLlmApiStyle.V1, false, false)] [InlineData(AzureLlmApiStyle.V1, false, true)] [InlineData(AzureLlmApiStyle.V1, true, false)] [InlineData(AzureLlmApiStyle.V1, true, true)]
    [InlineData(AzureLlmApiStyle.Dated, false, false)] [InlineData(AzureLlmApiStyle.Dated, false, true)] [InlineData(AzureLlmApiStyle.Dated, true, false)] [InlineData(AzureLlmApiStyle.Dated, true, true)]
    public async Task CompleteWireAndResults_MatchNativeGoldens(AzureLlmApiStyle style, bool responses, bool external)
    {
        var clock = new AzureClock(); var source = new AzureTokenSource(clock);
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var path = style == AzureLlmApiStyle.V1 ? "v1/" + (responses ? "responses" : "chat/completions") : (responses ? "responses" : "deployments/deploy.1/chat/completions") + "?api-version=2025-04-01-preview";
            Assert.Equal("https://primary.invalid/proxy/openai/" + path, request.RequestUri!.AbsoluteUri); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            if (external) { Assert.Equal("Bearer fixture-token-1", request.Headers.Authorization!.ToString()); Assert.False(request.Headers.Contains("api-key")); }
            else { Assert.Null(request.Headers.Authorization); Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("api-key"))); }
            var expected = JsonNode.Parse(AzureFixture.Text(responses ? "responses-wire" : style == AzureLlmApiStyle.V1 ? "chat-v1-wire" : "chat-dated-wire"));
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return AzureFixture.Reply(responses);
        });
        var factory = new AzureHttpFactory(handler); var provider = new AzureLlmProvider(factory, new() { ApiStyle = style, ApiVersion = style == AzureLlmApiStyle.Dated ? "2025-04-01-preview" : null }, source, timeProvider: clock);
        await using (var client = AzureFixture.Client(provider, responses, style, external))
        {
            var request = AzureFixture.Request(responses); var original = request.ToJsonString(); var result = await AzureFixture.Invoke(client, responses, request);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(AzureFixture.Response(responses)), result.Data)); Assert.Equal("deploy.1", result.Metadata.UpstreamModel);
            Assert.Equal("native-model-version", result.Data.GetProperty("model").GetString()); Assert.Equal(original, request.ToJsonString()); Assert.True(Assert.Single(factory.Clients).Disposed);
        }
        Assert.Equal(external ? 1 : 0, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed); Assert.Equal(1, handler.Calls); Assert.Equal("Asgard.Loki.LLM.Azure", Assert.Single(factory.Names));
    }
    /// <summary>令牌默认显示/JSON 不含材料，实际主体与到期时间仍由供应商检查。</summary>
    [Fact]
    public void AccessToken_DefaultFormattingIsSecretFree()
    {
        var value = new AzureLlmAccessToken("tenant/principal", "fixture-secret", new AzureClock().Now.AddHours(1)); var json = JsonSerializer.Serialize(value);
        Assert.Equal(nameof(AzureLlmAccessToken), value.ToString()); Assert.DoesNotContain("fixture-secret", json); Assert.DoesNotContain("tenant/principal", json);
    }
}
