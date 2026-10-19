using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>Bearer 的全部原生最终 HTTP/载荷/结果/事件/帧与独立完整样本比较。</summary>
public sealed class ApiKeyNativeTests
{
    public static TheoryData<bool, bool, bool, bool> Calls
    {
        get { TheoryData<bool, bool, bool, bool> rows = []; foreach (var converse in new[] { false, true }) foreach (var stream in new[] { false, true }) foreach (var arn in new[] { false, true }) foreach (var sdk in new[] { false, true }) rows.Add(converse, stream, arn, sdk); return rows; }
    }
    /// <summary>四个操作、模型/ARN 和公共/直接入口不生成签名头，也不改变原生正文或二进制状态。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task FullWireAndNativeData_MatchIndependentGoldens(bool converse, bool stream, bool arn, bool publicSdk)
    {
        var manifest = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("api-key-wire")).EnumerateArray().Single(item => item.GetProperty("converse").GetBoolean() == converse && item.GetProperty("stream").GetBoolean() == stream && item.GetProperty("arn").GetBoolean() == arn); var bytes = ApiKeyFixture.Bytes(converse, stream); var body = new OllamaBodyStream(bytes); var target = ApiKeyFixture.Target(converse) with { UpstreamModel = manifest.GetProperty("model").GetString()! };
        using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal(manifest.GetProperty("method").GetString(), request.Method.Method); Assert.Equal(manifest.GetProperty("uri").GetString(), request.RequestUri!.AbsoluteUri); foreach (var header in manifest.GetProperty("headers").EnumerateObject()) Assert.Equal(header.Value.GetString(), Assert.Single(header.Name == "Content-Type" ? request.Content!.Headers.GetValues(header.Name) : request.Headers.GetValues(header.Name))); Assert.False(request.Headers.Contains("x-amz-date")); Assert.False(request.Headers.Contains("x-amz-content-sha256")); Assert.False(request.Headers.Contains("x-amz-security-token")); Assert.Equal(stream && !converse, request.Headers.Contains("x-amzn-bedrock-accept")); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text(manifest.GetProperty("wireFixture").GetString()!)), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ApiKeyFixture.Reply(stream, body); }); var factory = new AwsHttpFactory(handler) { DefaultHeader = "Authorization" }; var provider = new AwsLlmProvider(factory, new() { Region = "us-east-1", RequestHeaders = new Dictionary<string, string> { ["X-Amzn-Bedrock-Trace"] = "ENABLED", ["x-amzn-bedrock-future-option"] = "  first   second  " } }); await using var client = ApiKeyFixture.Client(provider, converse, targets: [target]); var input = ApiKeyFixture.Request(converse); var before = input.ToJsonString(); var context = ApiKeyFixture.Context(converse, target);
        if (!stream)
        {
            var result = publicSdk ? await ApiKeyFixture.PublicComplete(client, converse, TestContext.Current.CancellationToken) : await ApiKeyFixture.Complete(provider, converse, input, context, TestContext.Current.CancellationToken); Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text(manifest.GetProperty("responseFixture").GetString()!)), result.Data)); Assert.Equal(target.UpstreamModel, result.Metadata.UpstreamModel); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
        }
        else
        {
            var events = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text(manifest.GetProperty("eventsFixture").GetString()!)); var frames = JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text(manifest.GetProperty("framesFixture").GetString()!)); var at = 0; var call = publicSdk ? ApiKeyFixture.PublicStream(client, converse, TestContext.Current.CancellationToken) : ApiKeyFixture.Stream(provider, converse, input, context, TestContext.Current.CancellationToken);
            await foreach (var item in call) { var expected = converse ? events[at].GetProperty("data") : events[at]; Assert.True(JsonElement.DeepEquals(expected, item.Data)); Assert.Equal(converse ? events[at].GetProperty("eventName").GetString() : at == 14 ? "future_transport" : "chunk", item.EventName); Assert.Equal(at >= (converse ? 24 : 11), body.Eof); Assert.Equal(body.Eof, body.Disposed); Assert.Equal(body.Eof, Assert.Single(factory.Clients).Disposed); var frame = item.GetAwsEventStreamFrame(); Assert.NotNull(frame); Assert.True(JsonElement.DeepEquals(frames[at].GetProperty("headers"), frame.Headers)); Assert.Equal(Convert.FromBase64String(frames[at].GetProperty("payloadBase64").GetString()!), frame.Payload.ToArray()); Assert.Equal(bytes.AsSpan(frames[at].GetProperty("offset").GetInt32(), frames[at].GetProperty("length").GetInt32()).ToArray(), frame.WireBytes.ToArray()); Assert.Equal(target.UpstreamModel, item.Metadata.UpstreamModel); at++; } Assert.Equal(converse ? 27 : 16, at);
        }
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>无签名来源也必须在工厂可变回调之前复制请求和原生头集合。</summary>
    [Theory] [MemberData(nameof(ApiKeyFixture.Operations), MemberType = typeof(ApiKeyFixture))]
    public async Task Snapshot_PrecedesFactoryCallbacks(bool converse, bool stream)
    {
        var input = ApiKeyFixture.Request(converse); var headers = new Dictionary<string, string> { ["x-amzn-bedrock-trace"] = "ENABLED" }; using var handler = new StubHttpHandler(async (request, token) => { Assert.Equal("ENABLED", Assert.Single(request.Headers.GetValues("x-amzn-bedrock-trace"))); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AwsFixture.Text(converse ? "converse-wire" : "wire")), JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)))); return ApiKeyFixture.Reply(stream, new OllamaBodyStream(ApiKeyFixture.Bytes(converse, stream))); }); var factory = new AwsHttpFactory(handler) { OnCreate = () => { input["messages"] = new JsonArray(); headers["x-amzn-bedrock-trace"] = "changed"; } }; var provider = new AwsLlmProvider(factory, new() { Region = "us-east-1", RequestHeaders = headers }); await ApiKeyFixture.Invoke(provider, converse, stream, TestContext.Current.CancellationToken, input: input); Assert.Equal(1, handler.Calls);
    }
}
