using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.AWS.Internal;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>完整最终 HTTP/签名/原生结果与独立样本逐项比较。</summary>
public sealed class NativeTests
{
    /// <summary>模型 ID 和 ARN 单段转义、临时令牌及原生扩展头都使用独立完整签名预期。</summary>
    [Theory] [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, false)] [InlineData(false, false, true)] [InlineData(false, true, true)] [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task CompleteWireAndSignature_MatchIndependentGoldens(bool arn, bool temporary, bool publicSdk)
    {
        var expected = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("signatures")).EnumerateArray().Single(item => item.GetProperty("publicSdk").GetBoolean() == publicSdk && item.GetProperty("arn").GetBoolean() == arn && item.GetProperty("temporary").GetBoolean() == temporary); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (temporary) source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "fixture-session")); var body = new OllamaBodyStream(Encoding.UTF8.GetBytes(AwsFixture.Text("response")));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal(expected.GetProperty("uri").GetString(), request.RequestUri!.AbsoluteUri); Assert.Equal(expected.GetProperty("canonicalPath").GetString(), AwsSigV4.CanonicalPath(request.RequestUri)); Assert.Equal("first.invalid", request.Headers.Host); Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType); Assert.Equal("application/json", request.Content!.Headers.ContentType!.ToString()); Assert.Equal("20150830T123600Z", Assert.Single(request.Headers.GetValues("x-amz-date"))); Assert.Equal(expected.GetProperty("payloadHash").GetString(), Assert.Single(request.Headers.GetValues("x-amz-content-sha256"))); Assert.Equal(expected.GetProperty("authorization").GetString(), Assert.Single(request.Headers.GetValues("Authorization"))); Assert.Equal(temporary, request.Headers.Contains("x-amz-security-token")); if (temporary) Assert.Equal("fixture-session", Assert.Single(request.Headers.GetValues("x-amz-security-token"))); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("wire")), JsonNode.Parse(await request.Content.ReadAsStringAsync(token)))); return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(body) };
        }); var factory = new AwsHttpFactory(handler) { DefaultHeader = "Authorization" }; var headers = temporary ? new Dictionary<string, string> { ["X-Amzn-Bedrock-Trace"] = "ENABLED", ["x-amzn-bedrock-future-option"] = "  first   second  " } : []; var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = headers }, timeProvider: clock); var target = AwsFixture.Target() with { UpstreamModel = arn ? "arn:aws:bedrock:us-east-1:123456789012:inference-profile/profile:0" : AwsFixture.Model }; await using var client = AwsFixture.Client(provider, targets: [target]); var result = publicSdk ? await AwsFixture.Invoke(client) : await provider.CreateAsync(AwsFixture.Request(), AwsFixture.Context(target), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("response")), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.Equal("native-returned-model", result.Data.GetProperty("model").GetString()); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.Equal("Asgard.Loki.LLM.AWS", Assert.Single(factory.Names)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
    /// <summary>调用者原始对象不被更改，认证等待期间的后续改动不进入已选请求。</summary>
    [Fact]
    public async Task InputSnapshot_PrecedesCredentialAwait()
    {
        var clock = new AwsClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var source = new AwsCredentialSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1)); } }; var input = AwsFixture.Request(); var before = input.ToJsonString(); using var handler = new StubHttpHandler(async (request, token) => { Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text("wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return AwsFixture.Reply(); }); var factory = new AwsHttpFactory(handler); var call = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock).CreateAsync(input, AwsFixture.Context(), TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken); Assert.Equal(before, input.ToJsonString()); input["messages"] = new JsonArray(); release.SetResult(); await call; Assert.Equal(1, handler.Calls);
    }
    /// <summary>未交付的二进制流操作在取得来源/HTTP 前明确失败。</summary>
    [Fact]
    public void BinaryEventStream_IsExplicitBoundary()
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new AwsHttpFactory(handler); Assert.Throws<NotSupportedException>(() => new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }).StreamAsync(AwsFixture.Request(), AwsFixture.Context(), TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients);
    }
}
