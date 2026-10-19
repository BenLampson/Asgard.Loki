using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.ConverseTextFixtures;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>四个公共文本入口实际走固定 Converse HTTP，工具及格式转换与原生认证仍分别归属原包。</summary>
public sealed class ConverseTextSdkTests
{
    /// <summary>每个公开协议独立覆盖 Runtime Bearer 与借用凭据源的 SigV4。</summary>
    public static IEnumerable<object[]> Calls()
    {
        foreach (var protocol in new[] { LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini })
        foreach (var apiKey in new[] { false, true }) yield return [protocol, apiKey];
    }
    /// <summary>完整最终正文独立比对，路由模型只进原生路径；响应原文及资源边界保持可查。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task PublicSdk_UsesNativeConverseAndCompleteGoldens(LlmProtocol protocol, bool apiKey)
    {
        var input = Read(protocol, "request"); var before = input.ToJsonString(); var expectedBody = Read(protocol, "nativeRequest"); expectedBody.Remove("model");
        var output = Read(protocol, "nativeResponse"); var body = new OllamaBodyStream(MantleFixture.Bytes(output.ToJsonString()));
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.EndsWith("/model/caller-model/converse", message.RequestUri!.AbsoluteUri);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Equal(expectedBody, JsonSerializer.Deserialize<JsonElement>(await message.Content.ReadAsStringAsync(token)));
            if (apiKey) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-native-key", message.Headers.Authorization.Parameter); }
            else Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", Assert.Single(message.Headers.GetValues("Authorization")));
            return MantleFixture.Reply(body, false);
        });
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit");
        await using var client = BedrockConversionFixture.Client(provider, protocol, apiKey);
        var result = await Complete(client, protocol, input, TestContext.Current.CancellationToken);
        var expected = Read(protocol, "response"); expected[protocol == LlmProtocol.Gemini ? "modelVersion" : "model"] = "caller-model";
        Assert.True(JsonNode.DeepEquals(expected, NormalizeResponse(result.Data, protocol)), result.Data.GetRawText());
        Assert.Equal(before, input.ToJsonString()); var conversion = result.Metadata.Conversion!; Assert.NotNull(conversion);
        Assert.Equal(LlmProtocol.BedrockConverse, conversion.UpstreamProtocol); Assert.Equal(protocol, conversion.PublicProtocol);
        Equal(output, conversion.UpstreamData); Assert.Empty(conversion.Diagnostics); Assert.Equal("caller-model", result.Metadata.UpstreamModel);
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed);
        Assert.Equal(apiKey ? 0 : 1, credentials.Calls); Assert.Equal(1, handler.Calls); Assert.False(credentials.Disposed); Assert.False(factory.Disposed);
    }

    /// <summary>混合流即使有完整调用接口，也不能取得签名材料、发送 HTTP 或自动改为非流。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task MixedStreams_FailBeforeCredentialsAndHttp(LlmProtocol protocol, bool apiKey)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit");
        await using var client = BedrockConversionFixture.Client(provider, protocol, apiKey);
        await Assert.ThrowsAsync<NotSupportedException>(() => Stream(client, protocol, Read(protocol, "request"), TestContext.Current.CancellationToken));
        Assert.Equal(0, credentials.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }

    /// <summary>源请求的严格字段错误必须在原生认证和 HTTP 之前报告。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task StrictRequestLoss_FailsBeforeCredentialsAndHttp(LlmProtocol protocol, bool apiKey)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit");
        await using var client = BedrockConversionFixture.Client(provider, protocol, apiKey);
        var input = Read(protocol, "request"); input["future_extension"] = null;
        var error = await Assert.ThrowsAsync<LlmConversionException>(() => Complete(client, protocol, input, TestContext.Current.CancellationToken));
        Assert.Equal("$.future_extension", Assert.Single(error.Diagnostics).Path);
        Assert.Equal(0, credentials.Calls); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }

    /// <summary>原生 EOF 读取和释放失败发生在转换结果交付前，不能返回一个成功的文本结果。</summary>
    [Theory] [MemberData(nameof(Calls))]
    public async Task NativeReadOrDisposalFailure_DoesNotPublishSuccess(LlmProtocol protocol, bool apiKey)
    {
        var output = Read(protocol, "nativeResponse"); var body = new OllamaBodyStream(MantleFixture.Bytes(output.ToJsonString()))
            { FailAtEof = apiKey, FailDispose = !apiKey };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(MantleFixture.Reply(body, false)));
        var factory = new AwsHttpFactory(handler); var clock = new AwsClock(); var credentials = new AwsCredentialSource(clock);
        var native = apiKey ? new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" })
            : new AwsLlmProvider(factory, credentials, new() { Region = "us-east-1" }, timeProvider: clock);
        var provider = new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit");
        await using var client = BedrockConversionFixture.Client(provider, protocol, apiKey);
        await Assert.ThrowsAsync<IOException>(() => Complete(client, protocol, Read(protocol, "request"), TestContext.Current.CancellationToken));
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>现有核心的目标与 Session 机制选定模型，装饰器只执行协议转换。</summary>
    private static async Task<LlmJsonResult> Complete(ILokiClient client, LlmProtocol protocol, JsonObject input, CancellationToken token) => protocol switch
    {
        LlmProtocol.ChatCompletions => await client.LLM.ChatCompletions.CompleteAsync(input.Deserialize<ChatCompletionRequest>()!, new() { SessionId = "text-converse" }, token),
        LlmProtocol.Responses => await client.LLM.Responses.CreateAsync(input.Deserialize<ResponseRequest>()!, new() { SessionId = "text-converse" }, token),
        LlmProtocol.ClaudeMessages => await client.LLM.ClaudeMessages.CreateAsync(input.Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "text-converse" }, token),
        _ => await client.LLM.Gemini.GenerateAsync(input.Deserialize<GeminiContentRequest>()!, new() { SessionId = "text-converse" }, token)
    };
    /// <summary>调用真实公共流入口并尝试枚举，避免只检查返回类型。</summary>
    private static Task Stream(ILokiClient client, LlmProtocol protocol, JsonObject input, CancellationToken token) => protocol switch
    {
        LlmProtocol.ChatCompletions => Drain(client.LLM.ChatCompletions.StreamAsync(input.Deserialize<ChatCompletionRequest>()!, new() { SessionId = "text-stream" }, token)),
        LlmProtocol.Responses => Drain(client.LLM.Responses.StreamAsync(input.Deserialize<ResponseRequest>()!, new() { SessionId = "text-stream" }, token)),
        LlmProtocol.ClaudeMessages => Drain(client.LLM.ClaudeMessages.StreamAsync(input.Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "text-stream" }, token)),
        _ => Drain(client.LLM.Gemini.StreamAsync(input.Deserialize<GeminiContentRequest>()!, new() { SessionId = "text-stream" }, token))
    };
    /// <summary>不依赖特定事件类型，失败仍必须来自真实枚举路径。</summary>
    private static async Task Drain<T>(IAsyncEnumerable<T> source) { await foreach (var _ in source) { } }
}
