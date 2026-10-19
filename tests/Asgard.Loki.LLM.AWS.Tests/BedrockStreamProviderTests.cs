using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.BedrockConversionFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>公共 SDK、真实 AWS 原生流适配器与显式转换的完整报文/事件验收。</summary>
public sealed class BedrockStreamProviderTests
{
    /// <summary>两个调用方向、明确版本/布局和两种认证都比较完整原生请求与逻辑事件。</summary>
    [Theory] [MemberData(nameof(BedrockCompleteConversionTests.Calls), MemberType = typeof(BedrockCompleteConversionTests))]
    public async Task PublicNativeStream_MatchesFullWireAndTargetGoldens(string version, bool nested, bool publicNova, bool apiKey)
    {
        var nativeNova = !publicNova; var converse = nested ? "ConverseNested" : "ConverseDirect";
        var request = Read(version, publicNova ? "Nova" : converse); var before = request.ToJsonString();
        var expectedRequest = Read(version, nativeNova ? "Nova" : converse); expectedRequest.Remove("model");
        var body = new OllamaBodyStream(BedrockStreamWire.Bytes(nativeNova));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/model/caller-model/" + (nativeNova ? "invoke-with-response-stream" : "converse-stream"), message.RequestUri!.AbsoluteUri);
            var actual = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.True(JsonNode.DeepEquals(expectedRequest, actual));
            Assert.False(actual!.AsObject().ContainsKey("stream")); Assert.False(actual.AsObject().ContainsKey("model")); Assert.False(actual.AsObject().ContainsKey("anthropic_version"));
            Assert.Equal("application/vnd.amazon.eventstream", Assert.Single(message.Headers.Accept).MediaType);
            Assert.Equal(nativeNova, message.Headers.Contains("x-amzn-bedrock-accept"));
            if (apiKey) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-native-key", message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-amz-date")); }
            else { Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/bedrock/aws4_request,", Assert.Single(message.Headers.GetValues("Authorization"))); Assert.Equal("20150830T123600Z", Assert.Single(message.Headers.GetValues("x-amz-date"))); }
            return StreamFixture.Reply(body);
        });
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var factory = new AwsHttpFactory(handler);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" }) : new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        var upstream = nativeNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse; var publicly = publicNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse;
        var converted = new ConvertingBedrockLlmProvider(native, upstream, "explicit", Options(version, nested));
        await using var client = Client(converted, publicly, apiKey); List<LlmJsonEvent> seen = [];
        await foreach (var item in Events(client, publicNova, request, TestContext.Current.CancellationToken))
        {
            if (seen.Count == 0) Assert.False(body.Eof);
            if (item.Type is "messageStop" or "metadata") { Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); }
            seen.Add(item);
        }
        var expected = BedrockStreamWire.Rows(publicNova); var original = BedrockStreamWire.Rows(nativeNova); Assert.Equal(expected.Count, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected[i]["data"]), seen[i].Data));
            Assert.Equal(expected[i][publicNova ? "eventType" : "eventName"]!.GetValue<string>(), seen[i].Type);
            if (publicNova) Assert.Null(seen[i].EventName); else Assert.Equal(seen[i].Type, seen[i].EventName);
            Assert.Null(seen[i].GetAwsEventStreamFrame()); Assert.Equal(upstream, seen[i].Metadata.Conversion!.UpstreamProtocol); Assert.Equal(publicly, seen[i].Metadata.Conversion!.PublicProtocol);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(original[i]["data"]), seen[i].Metadata.Conversion!.UpstreamData)); Assert.Empty(seen[i].Metadata.Conversion!.Diagnostics);
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(apiKey ? 0 : 1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }

    /// <summary>已知事件的业务错误扩展和迟到未知元数据保留损耗诊断，不伪造错误/结束类型。</summary>
    [Theory] [InlineData(true, false)] [InlineData(true, true)] [InlineData(false, false)] [InlineData(false, true)]
    public async Task UnknownMetadata_PreservesStrictAndConsentedBoundaries(bool publicNova, bool allowLoss)
    {
        var nativeNova = !publicNova; var rows = BedrockStreamWire.Rows(nativeNova);
        BedrockStreamWire.Payload(rows[2], nativeNova)["error"] = JsonNode.Parse("""{"type":"error","n":9007199254740993}""");
        var unknown = new JsonObject { ["eventName"] = "vendor.future", ["data"] = new JsonObject { ["type"] = "messageStop", ["error"] = false } }; rows.Insert(14, unknown);
        var body = new OllamaBodyStream(BedrockStreamWire.Bytes(nativeNova, rows)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body)));
        var native = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" });
        var provider = new ConvertingBedrockLlmProvider(native, nativeNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, "explicit", Options() with { AllowLoss = allowLoss });
        await using var client = Client(provider, publicNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, true); List<LlmJsonEvent> seen = [];
        if (!allowLoss)
        {
            var failure = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, publicNova, seen, TestContext.Current.CancellationToken));
            Assert.DoesNotContain(failure.Diagnostics, item => item.Code == "upstream_stream_error"); NoTerminal(seen);
        }
        else
        {
            await Collect(client, publicNova, seen, TestContext.Current.CancellationToken); Assert.Equal(15, seen.Count);
            Assert.Contains(seen[^1].Metadata.Conversion!.Diagnostics, item => item.Code == "unknown_event"); Assert.Contains(seen[^1].Metadata.Conversion!.Diagnostics, item => item.Path == "$event.error");
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(rows[2]["data"]), seen[2].Metadata.Conversion!.UpstreamData));
        }
        Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>按公共原生入口消费，不通过测试帮助函数重建结果。</summary>
    internal static async IAsyncEnumerable<LlmJsonEvent> Events(Asgard.Loki.Abstractions.ILokiClient client, bool nova, JsonObject request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (nova) { await foreach (var item in client.LLM.BedrockNova.StreamAsync(request.Deserialize<BedrockNovaRequest>()!, new() { SessionId = "bedrock-stream" }, token)) yield return item; }
        else { await foreach (var item in client.LLM.BedrockConverse.StreamAsync(request.Deserialize<BedrockConverseRequest>()!, new() { SessionId = "bedrock-stream" }, token)) yield return item; }
    }
    /// <summary>错误用例保留提前交付的增量，证明没有成功收尾。</summary>
    internal static async Task Collect(Asgard.Loki.Abstractions.ILokiClient client, bool nova, List<LlmJsonEvent> seen, CancellationToken token)
    { await foreach (var item in Events(client, nova, Read("v2", nova ? "Nova" : "ConverseDirect"), token)) seen.Add(item); }
    /// <summary>早期增量允许已经交付，消息结束和用量尾帧不得提前冒充成功。</summary>
    internal static void NoTerminal(IEnumerable<LlmJsonEvent> seen) => Assert.DoesNotContain(seen, item => item.Type is "messageStop" or "metadata");
}
