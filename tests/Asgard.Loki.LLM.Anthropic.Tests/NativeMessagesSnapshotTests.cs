using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Anthropic.Tests.AnthropicHttpFixture;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>完整原生 Messages、Runtime 和 Mantle 样本，分别验证直接适配器和公共 SDK。</summary>
public sealed class NativeMessagesSnapshotTests
{
    /// <summary>认证、入口和流模式分别明确选择，不从模型推断。</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var name in new[] { "anthropic", "bedrock-mantle", "bedrock-runtime" })
            foreach (var bearer in new[] { false, true }) foreach (var stream in new[] { false, true }) foreach (var sdk in new[] { false, true })
                yield return [name, bearer, stream, sdk];
    }
    /// <summary>每个字段和逐帧元数据匹配独立清单，工具关联、推理签名和大整数不丢失。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task NativeWire_MatchesIndependentManifest(string name, bool bearer, bool stream, bool sdk)
    {
        var golden = Golden(name); var expected = golden.GetProperty(stream ? "streamRequest" : "completeRequest");
        var wire = stream ? golden.GetProperty("wire").GetString()! : golden.GetProperty("complete").GetRawText(); var body = new OllamaBodyStream(Bytes(wire));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal(golden.GetProperty("path").GetString(), request.RequestUri!.AbsoluteUri); Assert.Equal(request.RequestUri.Authority, request.Headers.Host);
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            if (bearer) { Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("test-key", request.Headers.Authorization.Parameter); Assert.False(request.Headers.Contains("x-api-key")); }
            else { Assert.Null(request.Headers.Authorization); Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key"))); }
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(expected, json.RootElement));
            Assert.Equal("9007199254740993", json.RootElement.GetProperty("future").GetProperty("integer").GetRawText());
            Assert.False(json.RootElement.TryGetProperty("anthropic_version", out _)); Assert.Contains("fixture-beta", request.Headers.GetValues("anthropic-beta"));
            return Reply(body, stream, Bytes(wire).Length);
        });
        var factory = new AnthropicHttpFactory(handler) { Defaults = client =>
        {
            client.DefaultRequestHeaders.Add(bearer ? "Authorization" : "x-api-key", bearer ? "Bearer stale" : "stale");
            client.DefaultRequestHeaders.Add("anthropic-version", "stale"); client.DefaultRequestHeaders.Add("anthropic-beta", "fixture-beta"); client.DefaultRequestHeaders.Host = "stale.invalid";
        } };
        var provider = new AnthropicLlmProvider(factory, new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey });
        var target = Target(provider, golden.GetProperty("root").GetString()!, golden.GetProperty("model").GetString()!);
        var payload = JsonNode.Parse(expected.GetRawText())!.AsObject(); List<ClaudeMessageEvent> seen = []; JsonElement result = default;
        if (sdk)
        {
            await using var client = Build(provider, target: target); payload["model"] = "public"; var request = payload.Deserialize<ClaudeMessageRequest>()!;
            if (stream) await foreach (var item in client.LLM.ClaudeMessages.StreamAsync(request, new() { SessionId = "session" }, TestContext.Current.CancellationToken)) seen.Add(item);
            else result = (await client.LLM.ClaudeMessages.CreateAsync(request, new() { SessionId = "session" }, TestContext.Current.CancellationToken)).Data;
            Assert.Equal("public", payload["model"]!.GetValue<string>());
        }
        else
        {
            var context = new LlmAdapterContext(target, new("key", "test-key"), "public", "session");
            if (stream) await foreach (var item in provider.StreamAsync(payload, context, TestContext.Current.CancellationToken)) seen.Add(item);
            else result = (await provider.CreateAsync(payload, context, TestContext.Current.CancellationToken)).Data;
            Assert.True(JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement(payload)));
        }
        if (stream)
        {
            var frames = golden.GetProperty("events"); Assert.Equal(frames.GetArrayLength(), seen.Count);
            for (var i = 0; i < seen.Count; i++)
            {
                Assert.True(JsonElement.DeepEquals(frames[i].GetProperty("data"), seen[i].Data)); Assert.Equal(frames[i].GetProperty("event").GetString(), seen[i].EventName);
                Assert.Equal(frames[i].GetProperty("id").GetString(), seen[i].EventId); Assert.Equal(frames[i].GetProperty("retry").GetInt64(), seen[i].RetryMilliseconds);
                Assert.Equal(target.UpstreamModel, seen[i].Metadata.UpstreamModel); Assert.Null(seen[i].Metadata.Conversion);
            }
            Assert.Equal("9007199254740993", seen[^1].Data.GetProperty("future").GetProperty("integer").GetRawText());
        }
        else { Assert.True(JsonElement.DeepEquals(golden.GetProperty("complete"), result)); Assert.Equal("9007199254740993", result.GetProperty("future").GetProperty("integer").GetRawText()); }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
