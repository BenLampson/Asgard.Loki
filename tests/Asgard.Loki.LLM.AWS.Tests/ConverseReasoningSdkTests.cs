using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>模型设置与严格输出 Schema 通过既有完整目标到达原生 Converse，不把普通文本响应当作推理签名验证。</summary>
public sealed class ConverseReasoningSdkTests
{
    /// <summary>四协议、两个模型方言、两种认证均使用独立全量正文/响应快照。</summary>
    public static IEnumerable<object[]> Calls()
    {
        foreach (var item in Fixture()["cases"]!.AsArray()) foreach (var key in new[] { false, true })
            yield return [item!["name"]!.GetValue<string>(), key];
    }
    /// <summary>包装位置、预算/effort、Schema 原文和原生响应不在实际 HTTP 路径丢失。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicSdk_TransmitsExplicitSettingsWithStructuredOutput(string name, bool apiKey)
    {
        var fixture = Fixture(); var sample = Case(fixture, name); var protocol = Enum.Parse<LlmProtocol>(sample["protocol"]!.GetValue<string>());
        var input = sample["source"]!.AsObject(); var before = input.ToJsonString(); var expectedBody = sample["expected"]!.AsObject(); expectedBody.Remove("model");
        var output = fixture["nativeResponse"]!.AsObject();
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.EndsWith("/model/caller-model/converse", request.RequestUri!.AbsoluteUri);
            ConverseTextFixtures.Equal(expectedBody, JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token)));
            if (apiKey) Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            else Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", Assert.Single(request.Headers.GetValues("Authorization")));
            return Reply(output);
        });
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", Options(sample));
        await using var client = BedrockConversionFixture.Client(provider, protocol, apiKey);
        var result = await Complete(client, protocol, input, TestContext.Current.CancellationToken);
        var expected = fixture["responses"]![protocol.ToString()]!.AsObject(); expected[protocol == LlmProtocol.Gemini ? "modelVersion" : "model"] = "caller-model";
        Assert.True(JsonNode.DeepEquals(expected, ConverseTextFixtures.NormalizeResponse(result.Data, protocol)), result.Data.GetRawText());
        ConverseTextFixtures.Equal(output, result.Metadata.Conversion!.UpstreamData); Assert.Equal(before, input.ToJsonString());
        Assert.Equal(sample["diagnostics"]!.AsArray().Select(item => (item!["code"]!.GetValue<string>(), item["path"]!.GetValue<string>())),
            result.Metadata.Conversion.Diagnostics.Select(item => (item.Code, item.Path)));
        Assert.Equal(1, handler.Calls); Assert.Equal(apiKey ? 0 : 1, credentials.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.False(factory.Disposed); Assert.False(credentials.Disposed);
    }

    /// <summary>缺少显式模型方言或预算替代未获损耗许可时，不接触上游凭据及 HTTP。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task MissingPolicyOrLossConsent_FailsBeforeNativeAcquisition(string name, bool apiKey)
    {
        var sample = Case(Fixture(), name); var protocol = Enum.Parse<LlmProtocol>(sample["protocol"]!.GetValue<string>());
        var options = Options(sample); options = options.AllowLoss ? options with { AllowLoss = false }
            : options with { BedrockConverseModelDialect = BedrockConverseModelDialect.Unspecified };
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options);
        await using var client = BedrockConversionFixture.Client(provider, protocol, apiKey);
        await Assert.ThrowsAsync<LlmConversionException>(() => Complete(client, protocol, sample["source"]!.AsObject(), TestContext.Current.CancellationToken));
        Assert.Equal(0, credentials.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }

    /// <summary>方言和参数包装属于完整目标实现身份，不能在既有 Session 中重新声明。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ChangedModelPolicy_RejectsExistingSessionBeforeHttp(bool layout, bool apiKey)
    {
        var fixture = Fixture(); var sample = Case(fixture, layout ? "nova2-responses-low-nested" : "anthropic-chat-low");
        var protocol = Enum.Parse<LlmProtocol>(sample["protocol"]!.GetValue<string>()); var options = Options(sample);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(fixture["nativeResponse"]!.AsObject())));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var store = AwsFixture.Store();
        await using (var first = BedrockConversionFixture.Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options), protocol, apiKey, store))
            await Complete(first, protocol, sample["source"]!.AsObject(), TestContext.Current.CancellationToken);
        options = layout ? options with { BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.Direct }
            : options with { BedrockConverseModelDialect = BedrockConverseModelDialect.AmazonNova2, BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.Direct };
        await using var second = BedrockConversionFixture.Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options), protocol, apiKey, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(second, protocol, sample["source"]!.AsObject(), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Equal(apiKey ? 0 : 1, credentials.Calls);
    }
    /// <summary>完整原生响应由独立测试数据提供。</summary>
    private static HttpResponseMessage Reply(JsonObject data) => MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(data.ToJsonString())), false);
    /// <summary>每次新读资源，变体不与其他测试共享可变节点。</summary>
    private static JsonObject Fixture()
    {
        using var stream = typeof(ConverseReasoningSdkTests).Assembly.GetManifestResourceStream("ConverseReasoningRequests.Fixture")!;
        return JsonNode.Parse(stream)!.AsObject();
    }
    /// <summary>仅按金样名称选择，不推断模型支持范围。</summary>
    private static JsonObject Case(JsonObject fixture, string name) => fixture["cases"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == name)!.AsObject();
    /// <summary>声明完整模型方言策略，不执行模型发现。</summary>
    private static LlmConversionOptions Options(JsonObject sample) => sample["options"]!.Deserialize<LlmConversionOptions>(new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    /// <summary>仅调用现有公共 SDK，转换不增加模型调用协调层。</summary>
    private static async Task<LlmJsonResult> Complete(ILokiClient client, LlmProtocol protocol, JsonObject input, CancellationToken token) => protocol switch
    {
        LlmProtocol.ChatCompletions => await client.LLM.ChatCompletions.CompleteAsync(input.Deserialize<ChatCompletionRequest>()!, new() { SessionId = "reasoning-settings" }, token),
        LlmProtocol.Responses => await client.LLM.Responses.CreateAsync(input.Deserialize<ResponseRequest>()!, new() { SessionId = "reasoning-settings" }, token),
        LlmProtocol.ClaudeMessages => await client.LLM.ClaudeMessages.CreateAsync(input.Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "reasoning-settings" }, token),
        _ => await client.LLM.Gemini.GenerateAsync(input.Deserialize<GeminiContentRequest>()!, new() { SessionId = "reasoning-settings" }, token)
    };
}
