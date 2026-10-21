using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>真实公共 SDK 两次调用保留签名完整历史；测试使用替代 HTTP/凭据，不验证真实模型签名。</summary>
public sealed class ConverseAnthropicReplayTests
{
    /// <summary>未声明正确方言的实际原生响应不能在损耗许可下丢弃签名后返回成功。</summary>
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task UndeclaredReplyDialect_FailsAfterSingleHttpCall(bool nova, bool apiKey)
    {
        var sample = Case(Fixture(), "signed"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(sample["nativeResponse"]!.AsObject())));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var options = new LlmConversionOptions { AllowLoss = true, BedrockConverseModelDialect = nova
            ? BedrockConverseModelDialect.AmazonNova2 : BedrockConverseModelDialect.Unspecified,
            BedrockConverseInferenceFormat = nova ? BedrockConverseInferenceFormat.Direct : BedrockConverseInferenceFormat.Unspecified };
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options);
        await using var client = BedrockConversionFixture.Client(provider, LlmProtocol.ClaudeMessages, apiKey);
        var input = ConverseTextFixtures.Read(LlmProtocol.ClaudeMessages, "request");
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => client.LLM.ClaudeMessages.CreateAsync(input.Deserialize<ClaudeMessageRequest>()!,
            new() { SessionId = "undeclared-signed-reply" }, TestContext.Current.CancellationToken));
        Assert.Equal("converse_content_not_representable", Assert.Single(error.Diagnostics).Code);
        Assert.Equal(1, handler.Calls); Assert.Equal(apiKey ? 0 : 1, credentials.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>每组完整金样同时覆盖借用 SigV4 身份及 Runtime Bearer。</summary>
    public static IEnumerable<object[]> Calls()
    {
        foreach (var sample in Fixture()["cases"]!.AsArray()) foreach (var apiKey in new[] { false, true })
            yield return [sample!["name"]!.GetValue<string>(), apiKey];
    }
    /// <summary>最终 HTTP 请求和实际 SDK 结果都完整比对，第二次历史使用第一次收到的原始内容块。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task TwoPublicCalls_PreserveFullSignedHistoryAndNativeBodies(string name, bool apiKey)
    {
        var fixture = Fixture(); var sample = Case(fixture, name); var turn = 0;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            var first = turn++ == 0; var expected = sample[first ? "nativeRequest" : "followupNativeRequest"]!.DeepClone().AsObject(); expected.Remove("model");
            ConverseTextFixtures.Equal(expected, JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsStringAsync(token)));
            Assert.EndsWith("/model/caller-model/converse", message.RequestUri!.AbsoluteUri);
            if (apiKey) Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            else Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", Assert.Single(message.Headers.GetValues("Authorization")));
            return Reply((first ? sample["nativeResponse"] : fixture["finalNativeResponse"])!.AsObject());
        });
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", Options);
        await using var client = BedrockConversionFixture.Client(provider, LlmProtocol.ClaudeMessages, apiKey);
        var token = TestContext.Current.CancellationToken; var call = new LlmCallOptions { SessionId = "signed-replay" };
        var input = sample["sourceRequest"]!.AsObject(); var original = input.ToJsonString();
        var first = await client.LLM.ClaudeMessages.CreateAsync(input.Deserialize<ClaudeMessageRequest>()!, call, token);
        var expectedReply = sample["claudeResponse"]!.DeepClone().AsObject(); expectedReply["model"] = "caller-model";
        Assert.True(JsonNode.DeepEquals(expectedReply, ConverseTextFixtures.NormalizeResponse(first.Data, LlmProtocol.ClaudeMessages)));
        ConverseTextFixtures.Equal(sample["nativeResponse"]!.AsObject(), first.Metadata.Conversion!.UpstreamData); Assert.Empty(first.Metadata.Conversion.Diagnostics);
        Assert.Equal(original, input.ToJsonString());
        var followup = input.DeepClone().AsObject(); var expectedFollowup = sample["followupSourceRequest"]!.AsObject();
        followup["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(first.Data.GetProperty("content").GetRawText()) });
        followup["messages"]!.AsArray().Add(expectedFollowup["messages"]!.AsArray().Last()!.DeepClone());
        Assert.True(JsonNode.DeepEquals(expectedFollowup, followup));
        var second = await client.LLM.ClaudeMessages.CreateAsync(followup.Deserialize<ClaudeMessageRequest>()!, call, token);
        Assert.Equal("final", second.Data.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(first.Metadata.TargetId, second.Metadata.TargetId); Assert.Equal(first.Metadata.UpstreamModel, second.Metadata.UpstreamModel);
        Assert.NotEqual(first.Id, second.Id); Assert.Empty(second.Metadata.Conversion!.Diagnostics);
        ConverseTextFixtures.Equal(fixture["finalNativeResponse"]!.AsObject(), second.Metadata.Conversion.UpstreamData);
        Assert.Equal(2, handler.Calls); Assert.Equal(apiKey ? 0 : 2, credentials.Calls); Assert.Equal(2, factory.Clients.Count);
        Assert.All(factory.Clients, item => Assert.True(item.Disposed)); Assert.False(factory.Disposed); Assert.False(credentials.Disposed);
    }

    /// <summary>请求中的错位角色、字段损耗或不完整签名在原生认证/HTTP 前失败。</summary>
    [Theory] [InlineData(false, "signature")] [InlineData(true, "signature")]
    [InlineData(false, "loss")] [InlineData(true, "loss")]
    public async Task InvalidSignedHistory_FailsBeforeNativeAcquisition(bool apiKey, string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var source = Case(Fixture(), "signed")["sourceRequest"]!.AsObject();
        if (kind == "signature") source["messages"]![1]!["content"]![0]!.AsObject().Remove("signature"); else source["future_extension"] = null;
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", Options with { AllowLoss = true });
        await using var client = BedrockConversionFixture.Client(provider, LlmProtocol.ClaudeMessages, apiKey);
        await Assert.ThrowsAsync<LlmConversionException>(() => client.LLM.ClaudeMessages.CreateAsync(source.Deserialize<ClaudeMessageRequest>()!,
            new() { SessionId = "signed-invalid" }, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, credentials.Calls);
    }

    /// <summary>原生推理正文读取、释放和最终取消失败不能先发布一个转换成功结果。</summary>
    [Theory] [InlineData(false, "read")] [InlineData(true, "read")] [InlineData(false, "dispose")] [InlineData(true, "dispose")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task SignedReplySourceFailures_DoNotPublishSuccess(bool apiKey, string kind)
    {
        var sample = Case(Fixture(), "mixed-tools"); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new OllamaBodyStream(MantleFixture.Bytes(sample["nativeResponse"]!.ToJsonString()))
            { FailAtEof = kind == "read", FailDispose = kind == "dispose", OnEof = kind == "cancel" ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(body, false)));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", Options);
        await using var client = BedrockConversionFixture.Client(provider, LlmProtocol.ClaudeMessages, apiKey);
        var error = await Record.ExceptionAsync(() => client.LLM.ClaudeMessages.CreateAsync(sample["sourceRequest"]!.Deserialize<ClaudeMessageRequest>()!,
            new() { SessionId = "signed-fault" }, cancellation.Token));
        if (kind == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.Equal(1, handler.Calls); Assert.Equal(apiKey ? 0 : 1, credentials.Calls); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
    }

    /// <summary>签名历史不能因重新声明模型方言而迁移既有绑定。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ChangedDialect_RejectsSignedSessionBeforeSecondCall(bool apiKey)
    {
        var sample = Case(Fixture(), "signed"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(sample["nativeResponse"]!.AsObject())));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var store = AwsFixture.Store(); var call = new LlmCallOptions { SessionId = "signed-binding" }; var token = TestContext.Current.CancellationToken;
        await using (var first = BedrockConversionFixture.Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", Options), LlmProtocol.ClaudeMessages, apiKey, store))
            await first.LLM.ClaudeMessages.CreateAsync(sample["sourceRequest"]!.Deserialize<ClaudeMessageRequest>()!, call, token);
        var changed = Options with { BedrockConverseModelDialect = BedrockConverseModelDialect.AmazonNova2, BedrockConverseInferenceFormat = BedrockConverseInferenceFormat.Direct };
        await using var second = BedrockConversionFixture.Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", changed), LlmProtocol.ClaudeMessages, apiKey, store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.LLM.ClaudeMessages.CreateAsync(sample["followupSourceRequest"]!.Deserialize<ClaudeMessageRequest>()!, call, token));
        Assert.Equal(1, handler.Calls); Assert.Equal(apiKey ? 0 : 1, credentials.Calls);
    }
    /// <summary>显式同模型方言，不进行模型/签名推断。</summary>
    private static LlmConversionOptions Options => new() { BedrockConverseModelDialect = BedrockConverseModelDialect.Anthropic };
    /// <summary>完整响应来自独立资源，不从转换实现倒推。</summary>
    private static HttpResponseMessage Reply(JsonObject data) => MantleFixture.Reply(new MemoryStream(MantleFixture.Bytes(data.ToJsonString())), false);
    /// <summary>每次取得独立资源快照。</summary>
    private static JsonObject Fixture()
    {
        using var stream = typeof(ConverseAnthropicReplayTests).Assembly.GetManifestResourceStream("ConverseAnthropicReasoning.Fixture")!;
        return JsonNode.Parse(stream)!.AsObject();
    }
    /// <summary>名称只选择测试资料，不推测供应商模型类型。</summary>
    private static JsonObject Case(JsonObject fixture, string name) => fixture["cases"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == name)!.AsObject();
}
