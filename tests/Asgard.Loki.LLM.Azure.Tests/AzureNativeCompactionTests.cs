using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Azure.Tests;

/// <summary>通过公共 SDK 保留 Azure 原生窗口、工具/推理关联、显式版本和独立认证。</summary>
public sealed class AzureNativeCompactionTests
{
    /// <summary>所有明确地址与认证组合匹配独立完整请求；拥有的引用和完整返回窗口原样保留。</summary>
    [Theory]
    [InlineData(0, false, 0)] [InlineData(0, false, 1)] [InlineData(0, true, 0)] [InlineData(0, true, 1)]
    [InlineData(1, false, 0)] [InlineData(1, false, 1)] [InlineData(1, true, 0)] [InlineData(1, true, 1)]
    [InlineData(2, false, 0)] [InlineData(2, false, 1)] [InlineData(2, true, 0)] [InlineData(2, true, 1)]
    public async Task FullWindowAndTargetMatchIndependentGolden(int profile, bool external, int index)
    {
        var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureCompactionFixture.Factory();
        using var stream = new AzureCompactionReadStream(Encoding.UTF8.GetBytes(AzureCompactionFixture.Part("response").GetRawText()));
        factory.OnSend = async (message, token) =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)) return AzureCompactionFixture.SeedReply();
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(AzureCompactionFixture.Part("profiles")[profile].GetProperty("endpoint").GetString(), message.RequestUri.AbsoluteUri);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            var raw = AzureCompactionFixture.Element(await message.Content.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(AzureCompactionFixture.Part("requests")[index], raw)); Assert.False(raw.TryGetProperty("stream", out _));
            if (external) { Assert.False(message.Headers.Contains("api-key")); Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-token-" + (index + 1), message.Headers.Authorization.Parameter); }
            else { Assert.Null(message.Headers.Authorization); Assert.Equal("fixture-key", Assert.Single(message.Headers.GetValues("api-key"))); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        var provider = new AzureLlmProvider(factory, AzureCompactionFixture.Options(profile), external ? source : null, timeProvider: clock);
        await using var client = AzureCompactionFixture.Build(provider, external, profile); var token = TestContext.Current.CancellationToken;
        if (index == 1) await AzureCompactionFixture.SeedAsync(client, token);
        var request = AzureCompactionFixture.Request(index); var before = JsonSerializer.Serialize(request); var result = await client.LLM.Responses.CompactAsync(request, AzureCompactionFixture.Call(), token);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonElement.DeepEquals(AzureCompactionFixture.Part("response"), result.Data));
        Assert.Equal(new LlmResultMetadata("public", "deploy.compact.1", "azure", "primary"), result.Metadata); Assert.Equal("call-retained", result.Output[2].GetProperty("call_id").GetString()); Assert.Equal("call-retained", result.Output[3].GetProperty("call_id").GetString());
        Assert.Equal("opaque-reasoning-after", result.Output[1].GetProperty("encrypted_content").GetString()); Assert.Equal("opaque-signed-window", result.Output[4].GetProperty("encrypted_content").GetString());
        Assert.Equal("9007199254740993", result.Data.GetProperty("future").GetProperty("exact").GetRawText()); Assert.True(stream.Eof); Assert.True(stream.Disposed);
        Assert.Equal(index + 1, factory.Calls); Assert.Equal(index + 1, factory.Disposals); Assert.False(factory.Disposed); Assert.False(source.Disposed); Assert.Equal(external ? index + 1 : 0, source.Calls);
    }
    /// <summary>认证源等待开始前冻结请求，后续清空外部 DOM 不改变已选择的上游正文。</summary>
    [Fact]
    public async Task SnapshotPrecedesExternalAuthenticationAwait()
    {
        var request = AzureCompactionFixture.Payload(); var clock = new AzureClock(); using var source = new AzureTokenSource(clock); using var factory = AzureCompactionFixture.Factory();
        source.Resolve = (principal, _) => { request.Clear(); return ValueTask.FromResult(new AzureLlmAccessToken(principal, "fixture-token", clock.Now.AddHours(1))); };
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(AzureCompactionFixture.Part("requests")[0], AzureCompactionFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return AzureCompactionFixture.Json(AzureCompactionFixture.Part("response")); };
        var provider = new AzureLlmProvider(factory, tokenSource: source, timeProvider: clock);
        await provider.CompactAsync(request, AzureCompactionFixture.Context(external: true), TestContext.Current.CancellationToken); Assert.Empty(request); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生 HTTP200/HTTP 错误和未知字段保留，不自动重试或重新取令牌。</summary>
    [Theory] [InlineData(200)] [InlineData(400)] [InlineData(429)] [InlineData(503)]
    public async Task NativeErrorsRetainRawBytesAndActualStatus(int status)
    {
        var expected = AzureCompactionFixture.Part("native_error"); using var factory = AzureCompactionFixture.Factory(); factory.OnSend = (_, _) =>
        { var reply = AzureCompactionFixture.Json(expected, (HttpStatusCode)status); reply.Headers.TryAddWithoutValidation("Retry-After", "1"); return Task.FromResult(reply); };
        var provider = new AzureLlmProvider(factory); var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => provider.CompactAsync(AzureCompactionFixture.Payload(), AzureCompactionFixture.Context(), TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(expected.GetRawText()), error.Body.ToArray()); Assert.Equal(expected.GetRawText(), error.ResponseBody); Assert.Equal("application/json; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls);
    }
}
