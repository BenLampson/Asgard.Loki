using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.MantleFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>独立 Python SHA256/HMAC 向量及完整 Messages 报文验证真实 HTTP 边界。</summary>
public sealed class MantleNativeTests
{
    /// <summary>十六种完整 HTTP 向量覆盖两个根、原生/SDK、临时/长期凭据和 JSON/SSE。</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var proxy in new[] { false, true }) foreach (var sdk in new[] { false, true }) foreach (var temporary in new[] { false, true }) foreach (var stream in new[] { false, true }) yield return [proxy, sdk, temporary, stream];
    }
    /// <summary>签名使用实际 UTF-8 正文、Host、已选原生头和 bedrock-mantle 服务；完整原生字段保持一致。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task WireAndSignature_MatchIndependentManifest(bool proxy, bool sdk, bool temporary, bool stream)
    {
        var vector = Golden("signatures").EnumerateArray().Single(item => item.GetProperty("proxy").GetBoolean() == proxy && item.GetProperty("publicSdk").GetBoolean() == sdk && item.GetProperty("temporary").GetBoolean() == temporary && item.GetProperty("stream").GetBoolean() == stream);
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock);
        if (temporary) source.Resolve = (principal, _) => ValueTask.FromResult(new AwsLlmSigningCredentials(principal, AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1), "fixture-session"));
        var wire = stream ? Golden("stream").GetProperty("wire").GetString()! : Golden("response").GetRawText(); var body = new OllamaBodyStream(Bytes(wire));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(vector.GetProperty("uri").GetString(), request.RequestUri!.AbsoluteUri); Assert.Equal(vector.GetProperty("body").GetString(), await request.Content!.ReadAsStringAsync(token));
            var authorization = Assert.Single(request.Headers.GetValues("Authorization")); Assert.Equal(vector.GetProperty("authorization").GetString(), authorization); Assert.Contains("/bedrock-mantle/aws4_request", authorization);
            foreach (var header in vector.GetProperty("headers").EnumerateObject())
            {
                var actual = header.Name == "content-type" ? request.Content!.Headers.ContentType!.ToString() : Assert.Single(request.Headers.GetValues(header.Name)); Assert.Equal(header.Value.GetString(), actual);
            }
            Assert.False(request.Headers.Contains("x-api-key")); Assert.False(request.Headers.Contains("api-key")); Assert.False(request.Headers.Contains("x-goog-api-key"));
            if (!temporary) Assert.False(request.Headers.Contains("x-amz-security-token"));
            return Reply(body, stream, Bytes(wire).Length);
        });
        var factory = new AwsHttpFactory(handler); var provider = new AwsMantleLlmProvider(factory, source, new() { Region = "us-east-1", RequestHeaders = new Dictionary<string, string> { ["anthropic-beta"] = "fixture-beta", ["anthropic-workspace-id"] = "workspace-fixture", ["x-amzn-bedrock-future-option"] = "  first   second  " } }, timeProvider: clock);
        var target = Target(provider.Id, root: vector.GetProperty("root").GetString()!, model: vector.GetProperty("model").GetString()!); var input = Request(); var before = input.ToJsonString(); List<ClaudeMessageEvent> seen = []; JsonElement result = default;
        if (sdk)
        {
            await using var client = Client(provider, targets: [target]); var request = input.Deserialize<ClaudeMessageRequest>()!;
            if (stream) await foreach (var item in client.LLM.ClaudeMessages.StreamAsync(request, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken)) seen.Add(item);
            else result = (await client.LLM.ClaudeMessages.CreateAsync(request, new() { SessionId = "mantle-session" }, TestContext.Current.CancellationToken)).Data;
        }
        else if (stream) await foreach (var item in provider.StreamAsync(input, Context(target), TestContext.Current.CancellationToken)) seen.Add(item);
        else result = (await provider.CreateAsync(input, Context(target), TestContext.Current.CancellationToken)).Data;
        Assert.Equal(before, input.ToJsonString());
        if (stream)
        {
            var frames = Golden("stream").GetProperty("events"); Assert.Equal(frames.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++)
            {
                Assert.True(JsonElement.DeepEquals(frames[i].GetProperty("data"), seen[i].Data)); Assert.Equal(frames[i].GetProperty("event").GetString(), seen[i].EventName);
                Assert.Equal(frames[i].GetProperty("id").GetString(), seen[i].EventId); Assert.Equal(frames[i].GetProperty("retry").GetInt64(), seen[i].RetryMilliseconds); Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel);
                Assert.Null(seen[i].GetAwsEventStreamFrame());
            }
            Assert.Equal("9007199254740993", seen[^1].Data.GetProperty("future").GetProperty("integer").GetRawText());
        }
        else { Assert.True(JsonElement.DeepEquals(Golden("response"), result)); Assert.Equal("9007199254740993", result.GetProperty("future").GetProperty("integer").GetRawText()); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.Equal("Asgard.Loki.LLM.AWS.Mantle", Assert.Single(factory.Names)); Assert.False(factory.Disposed); Assert.False(source.Disposed);
    }
}
