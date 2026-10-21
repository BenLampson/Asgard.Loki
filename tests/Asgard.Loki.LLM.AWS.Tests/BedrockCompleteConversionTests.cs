using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.BedrockConversionFixture;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>通过公共 SDK 验证两个完整原生 API 的实际 JSON、认证委托、完整目标和资源边界。</summary>
public sealed class BedrockCompleteConversionTests
{
    /// <summary>两个装饰器保持各自协议声明；不能嵌套或由四协议装饰器冒充 Bedrock。</summary>
    [Fact]
    public void WrapperDeclarations_KeepProtocolFamiliesSeparate()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var native = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" });
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockNova, "explicit", Options());
        foreach (var protocol in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
        { Assert.False(provider.SupportsProtocol(protocol)); Assert.Throws<ArgumentException>(() => Client(provider, protocol, true)); }
        Assert.Throws<ArgumentException>(() => new ConvertingBedrockLlmProvider(provider, LlmProtocol.BedrockNova, "nested"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConvertingBedrockLlmProvider(native, LlmProtocol.ClaudeMessages, "wrong"));
        Assert.Throws<ArgumentException>(() => new ConvertingLlmProvider(native, LlmProtocol.BedrockNova, "wrong"));
        var four = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "four");
        Assert.Throws<ArgumentException>(() => new ConvertingBedrockLlmProvider(four, LlmProtocol.BedrockNova, "wrong"));
        Assert.Equal(0, handler.Calls);
    }
    /// <summary>两种外壳、两种布局、两个方向和两种原生认证都独立执行。</summary>
    public static IEnumerable<object[]> Calls()
    {
        foreach (var version in new[] { "v1", "v2" }) foreach (var nested in new[] { false, true })
        foreach (var nova in new[] { false, true }) foreach (var apiKey in new[] { false, true }) yield return [version, nested, nova, apiKey];
    }
    /// <summary>最终 HTTP 不带路由模型/stream；Schema、调用 ID、JSON 和原生输出逐字段保持。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicCompletePipeline_MatchesNativeGoldens(string version, bool nested, bool nova, bool apiKey)
    {
        var converse = nested ? "ConverseNested" : "ConverseDirect"; var input = Read(version, nova ? "Nova" : converse); var before = input.ToJsonString();
        var expected = Read(version, nova ? converse : "Nova"); expected.Remove("model"); var output = Read(version, nova ? "ConverseResponse" : "NovaResponse");
        var body = new OllamaBodyStream(MantleFixture.Bytes(output.ToJsonString()));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://first.invalid/proxy/model/caller-model/" + (nova ? "converse" : "invoke"), message.RequestUri!.AbsoluteUri);
            var actual = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.True(JsonNode.DeepEquals(expected, actual), actual!.ToJsonString());
            Assert.False(actual!.AsObject().ContainsKey("model")); Assert.False(actual.AsObject().ContainsKey("stream"));
            Assert.False(actual.AsObject().ContainsKey("anthropic_version")); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            if (apiKey) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-native-key", message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-amz-date")); }
            else { Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/bedrock/aws4_request,", Assert.Single(message.Headers.GetValues("Authorization"))); Assert.Equal("20150830T123600Z", Assert.Single(message.Headers.GetValues("x-amz-date"))); }
            return MantleFixture.Reply(body, false);
        });
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var factory = new AwsHttpFactory(handler);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock);
        var upstream = nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova; var publicly = nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse;
        var provider = new ConvertingBedrockLlmProvider(native, upstream, "explicit", Options(version, nested));
        await using var client = Client(provider, publicly, apiKey); var result = await Complete(client, nova, input, TestContext.Current.CancellationToken);
        Assert.Equal(before, input.ToJsonString()); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(output), result.Data));
        Assert.Equal(upstream, result.Metadata.Conversion!.UpstreamProtocol); Assert.Equal(publicly, result.Metadata.Conversion.PublicProtocol);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(output), result.Metadata.Conversion.UpstreamData)); Assert.Empty(result.Metadata.Conversion.Diagnostics);
        Assert.Equal("caller-model", result.Metadata.UpstreamModel); Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.Equal(apiKey ? 0 : 1, source.Calls); Assert.Equal(1, handler.Calls); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }

    /// <summary>请求配置、源内容和流映射缺口都在获取签名材料或创建 HTTP 客户端之前失败。</summary>
    [Theory] [InlineData(true, "format")] [InlineData(false, "format")]
    [InlineData(true, "layout")] [InlineData(false, "layout")]
    [InlineData(true, "document")] [InlineData(false, "document")]
    [InlineData(true, "stream-format")] [InlineData(false, "stream-format")]
    [InlineData(true, "high-limit")] [InlineData(false, "high-limit")]
    public async Task UnsupportedCalls_FailBeforeCredentialOrHttp(bool nova, string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var factory = new AwsHttpFactory(handler);
        var native = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var options = Options();
        if (kind is "format" or "stream-format") options = options with { BedrockNovaRequestFormat = BedrockNovaRequestFormat.Unspecified };
        if (kind == "layout") options = options with { BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.Unspecified };
        var provider = new ConvertingBedrockLlmProvider(native, nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, "explicit", options);
        await using var client = Client(provider, nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse); var input = Read("v2", nova ? "Nova" : "ConverseDirect");
        if (kind == "document") input["messages"]![0]!["content"]!.AsArray().Add(new JsonObject { ["document"] = new JsonObject() });
        if (kind == "high-limit")
        {
            var settings = nova ? input["inferenceConfig"]! : input["additionalModelRequestFields"]!;
            settings["reasoningConfig"]!["maxReasoningEffort"] = "high";
        }
        var failure = await Record.ExceptionAsync(async () => { if (kind == "stream-format") await Stream(client, nova, input, TestContext.Current.CancellationToken); else await Complete(client, nova, input, TestContext.Current.CancellationToken); });
        if (kind == "document") Assert.IsType<LlmConversionException>(failure); else Assert.IsType<ArgumentException>(failure);
        Assert.Equal(0, source.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }

    /// <summary>已有完整 Session 不能因调用者重新声明格式或布局而改绑。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ChangedExplicitFormatOrLayout_RejectsBoundSession(bool format)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(Read("v2", "ConverseResponse").ToJsonString())), false)));
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var native = new AwsLlmProvider(new AwsHttpFactory(handler), source, new() { Region = "us-east-1" }, timeProvider: clock);
        var store = AwsFixture.Store(); var options = Options();
        await using (var first = Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options), LlmProtocol.BedrockNova, store: store))
            await Complete(first, true, Read("v2", "Nova"), TestContext.Current.CancellationToken);
        options = format ? options with { BedrockNovaRequestFormat = BedrockNovaRequestFormat.MessagesV1 } : options with { BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.NestedInferenceConfig };
        await using var second = Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options), LlmProtocol.BedrockNova, store: store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(second, true, Read("v2", "Nova"), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Equal(1, source.Calls);
    }

    /// <summary>原生响应即使自身无重复，也不能在转换上下文中复用历史已消费的 ID。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ResponseReusingHistoricalCallId_RejectsWithoutRewriting(bool nova)
    {
        var output = Read("v2", nova ? "ConverseResponse" : "NovaResponse");
        output["output"]!["message"]!["content"]![1]!["toolUse"]!["toolUseId"] = "call-a";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(output.ToJsonString())), false)));
        var native = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" });
        var provider = new ConvertingBedrockLlmProvider(native, nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, "explicit", Options() with { AllowLoss = true });
        await using var client = Client(provider, nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, true);
        var failure = await Assert.ThrowsAsync<LlmConversionException>(() => Complete(client, nova, Read("v2", nova ? "Nova" : "ConverseDirect"), TestContext.Current.CancellationToken));
        Assert.Equal("tool_call_id_reused", Assert.Single(failure.Diagnostics).Code);
        Assert.Equal("call-a", output["output"]!["message"]!["content"]![1]!["toolUse"]!["toolUseId"]!.GetValue<string>()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>原生 JSON 的读取、释放和取消失败不能被转换成一个已成功返回的结果。</summary>
    [Theory] [InlineData(true, "read")] [InlineData(false, "read")]
    [InlineData(true, "dispose")] [InlineData(false, "dispose")]
    [InlineData(true, "cancel")] [InlineData(false, "cancel")]
    [InlineData(true, "http-error")] [InlineData(false, "http-error")]
    public async Task NativeFailures_DoNotReturnConvertedSuccess(bool nova, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = Read("v2", nova ? "ConverseResponse" : "NovaResponse").ToJsonString();
        var body = new OllamaBodyStream(MantleFixture.Bytes(output)) { FailAtEof = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(body, false, status: kind == "http-error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        var factory = new AwsHttpFactory(handler); var native = new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" });
        var provider = new ConvertingBedrockLlmProvider(native, nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, "explicit", Options());
        await using var client = Client(provider, nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, true);
        var failure = await Record.ExceptionAsync(() => Complete(client, nova, Read("v2", nova ? "Nova" : "ConverseDirect"), cancellation.Token));
        if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else if (kind == "http-error") { var error = Assert.IsType<LlmUpstreamException>(failure); Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode); Assert.Equal(output, error.ResponseBody); }
        else Assert.IsType<IOException>(failure);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>同协议流维持原始供应商事件对象及二进制附件，不经过 JSON 转换重建。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task NativeIdentityStream_PreservesBinaryFrameAttachments(bool nova)
    {
        var bytes = nova ? NovaStreamFixture.Bytes() : ConverseStreamFixture.Bytes(); var body = new OllamaBodyStream(bytes);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body)));
        var native = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }); var protocol = nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse;
        var provider = new ConvertingBedrockLlmProvider(native, protocol, "explicit"); await using var client = Client(provider, protocol, true);
        var events = await Stream(client, nova, Read("v2", nova ? "Nova" : "ConverseDirect"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(events); Assert.All(events, item => { Assert.NotNull(item.GetAwsEventStreamFrame()); Assert.Null(item.Metadata.Conversion); });
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
}
