using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Nova InvokeModel 的完整 HTTP、独立签名和原生输入/输出验证。</summary>
public sealed class NovaNativeTests
{
    /// <summary>v1/v2、普通模型/ARN、临时身份和直接/公共调用逐一验证。</summary>
    public static IEnumerable<object[]> SignedCases()
    {
        foreach (var version in new[] { "v1", "v2" }) foreach (var sdk in new[] { false, true }) foreach (var arn in new[] { false, true }) foreach (var temporary in new[] { false, true }) yield return [version, sdk, arn, temporary];
    }
    /// <summary>原生 Nova 的实际正文使用十六个独立 Python 签名预期，不能套用 Claude 版本。</summary>
    [Theory] [MemberData(nameof(SignedCases))]
    public async Task SignedWire_MatchesIndependentNativeVectors(string version, bool sdk, bool arn, bool temporary)
    {
        var vector = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("nova-signatures")).EnumerateArray().Single(item => item.GetProperty("version").GetString() == version && item.GetProperty("publicSdk").GetBoolean() == sdk && item.GetProperty("arn").GetBoolean() == arn && item.GetProperty("temporary").GetBoolean() == temporary);
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); if (temporary) source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "fixture-session"));
        var response = NovaFixture.Golden(version).GetProperty("response"); var body = new OllamaBodyStream(MantleFixture.Bytes(response.GetRawText()));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal(vector.GetProperty("uri").GetString(), message.RequestUri!.AbsoluteUri); Assert.Equal(vector.GetProperty("body").GetString(), await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(vector.GetProperty("authorization").GetString(), Assert.Single(message.Headers.GetValues("Authorization"))); Assert.Equal(vector.GetProperty("payloadHash").GetString(), Assert.Single(message.Headers.GetValues("x-amz-content-sha256")));
            Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType); Assert.False(message.Headers.Contains("anthropic-version")); Assert.False(message.Headers.Contains("x-api-key"));
            if (temporary) Assert.Equal("fixture-session", Assert.Single(message.Headers.GetValues("x-amz-security-token"))); else Assert.False(message.Headers.Contains("x-amz-security-token"));
            return MantleFixture.Reply(body, false, MantleFixture.Bytes(response.GetRawText()).Length);
        });
        var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = temporary ? new Dictionary<string, string> { ["x-amzn-bedrock-trace"] = "ENABLED" } : new Dictionary<string, string>() }, timeProvider: clock); var target = NovaFixture.Target(vector.GetProperty("model").GetString()!); var input = NovaFixture.Request(version); var before = input.ToJsonString();
        await using var client = NovaFixture.Client(provider, targets: [target]); var actual = sdk ? await client.LLM.BedrockNova.InvokeAsync(NovaFixture.Typed(version), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken) : await provider.InvokeNovaAsync(input, NovaFixture.Context(target), TestContext.Current.CancellationToken);
        Assert.Equal(before, input.ToJsonString()); Assert.True(JsonElement.DeepEquals(response, actual.Data)); Assert.Equal(target.UpstreamModel, actual.Metadata.UpstreamModel); Assert.Equal("9007199254740993", actual.Data.GetProperty("future").GetProperty("integer").GetRawText()); Assert.Null(actual.Metadata.Conversion);
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>相同原生正文通过明确 Bearer 入口调用，不获取 SigV4 身份或发送其默认头。</summary>
    [Theory] [InlineData("v1", false)] [InlineData("v1", true)] [InlineData("v2", false)] [InlineData("v2", true)]
    public async Task ApiKeyWire_PreservesNativeBody(string version, bool sdk)
    {
        var golden = NovaFixture.Golden(version); using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-nova-key", message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-amz-date")); Assert.False(message.Headers.Contains("x-amz-content-sha256")); Assert.False(message.Headers.Contains("x-amz-security-token"));
            var expected = JsonNode.Parse(golden.GetProperty("request").GetRawText())!.AsObject(); expected.Remove("model"); var actual = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.True(JsonNode.DeepEquals(expected, actual)); Assert.False(actual!.AsObject().ContainsKey("anthropic_version"));
            return MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(golden.GetProperty("response").GetRawText())), false);
        });
        var factory = new AwsHttpFactory(handler); var provider = new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" }); var target = NovaFixture.Target(version == "v1" ? "us.amazon.nova-lite-v1:0" : "us.amazon.nova-2-lite-v1:0"); await using var client = NovaFixture.Client(provider, true, targets: [target]);
        var result = sdk ? await client.LLM.BedrockNova.InvokeAsync(NovaFixture.Typed(version), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken) : await provider.InvokeNovaAsync(NovaFixture.Request(version), NovaFixture.Context(target, apiKey: true), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(golden.GetProperty("response"), result.Data)); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>正文快照先于签名来源等待，原生零/null/未知字段不会被迟到修改覆盖。</summary>
    [Fact]
    public async Task NativeSnapshot_PrecedesSourceAwait()
    {
        var clock = new AwsClock(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new AwsCredentialSource(clock) { Resolve = async (principal, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return new(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1)); } };
        var input = NovaFixture.Request("v2"); var expected = input.DeepClone().AsObject(); expected.Remove("model"); using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)))); return MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(NovaFixture.Golden("v2").GetProperty("response").GetRawText())), false);
        });
        var provider = new AwsLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock); var call = provider.InvokeNovaAsync(input, NovaFixture.Context(NovaFixture.Target("us.amazon.nova-2-lite-v1:0")), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken); input["messages"] = new JsonArray(); release.SetResult(); await call; Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
    /// <summary>v1 正文版本显式保留，v2 缺省不补入；未知 stream/版本/状态对象不被删除或挪动。</summary>
    [Theory] [InlineData("v1")] [InlineData("v2")]
    public async Task ExplicitUnknownFields_AreNotNormalized(string version)
    {
        var input = NovaFixture.Request(version); input["stream"] = "caller-native-extension"; input["anthropic_version"] = "caller-owned-field";
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!.AsObject(); Assert.Equal("caller-native-extension", body["stream"]!.GetValue<string>()); Assert.Equal("caller-owned-field", body["anthropic_version"]!.GetValue<string>()); Assert.Equal(version == "v1", body.ContainsKey("schemaVersion")); return MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes("{}")), false);
        });
        await new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }).InvokeNovaAsync(input, NovaFixture.Context(apiKey: true), TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
    /// <summary>未实现的原生二进制流在获取凭据/HTTP 前明确失败，不退化为其他方法。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedStreaming_FailsBeforeAcquisition(bool apiKey)
    {
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new AwsHttpFactory(handler); var provider = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" }) : new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        Assert.Throws<NotSupportedException>(() => ((IBedrockNovaAdapter)provider).StreamAsync(NovaFixture.Request(), NovaFixture.Context(apiKey: apiKey), TestContext.Current.CancellationToken));
        await using var client = NovaFixture.Client(provider, apiKey); await Assert.ThrowsAsync<NotSupportedException>(async () => { await foreach (var _ in client.LLM.BedrockNova.StreamAsync(NovaFixture.Typed(), new() { SessionId = "aws-session" }, TestContext.Current.CancellationToken)) { } }); Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
}
