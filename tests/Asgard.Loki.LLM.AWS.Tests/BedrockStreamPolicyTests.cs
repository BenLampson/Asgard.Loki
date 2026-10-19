using System.Text.Json;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.BedrockConversionFixture;
using static Asgard.Loki.LLM.AWS.Tests.BedrockStreamProviderTests;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>原生转换状态上限和完整目标身份，不改变既有缺省绑定或原生供应商依赖。</summary>
public sealed class BedrockStreamPolicyTests
{
    /// <summary>转换上限不是上游配额；原生 HTTP 成功也不能让本地状态超限继续完成。</summary>
    [Theory] [InlineData(true, "block")] [InlineData(false, "block")]
    [InlineData(true, "argument")] [InlineData(false, "argument")]
    [InlineData(true, "tail")] [InlineData(false, "tail")]
    [InlineData(true, "diagnostic")] [InlineData(false, "diagnostic")]
    public async Task LocalStateOverflow_DoesNotPublishSuccess(bool publicNova, string kind)
    {
        var nativeNova = !publicNova; var rows = BedrockStreamWire.Rows(nativeNova); var options = Options() with { AllowLoss = true };
        if (kind == "block") options = options with { MaxBedrockStreamBlocks = 3 };
        if (kind == "argument") options = options with { MaxBedrockStreamToolArgumentBytes = 4 };
        if (kind == "tail") options = options with { MaxBedrockStreamTailBytes = 32 };
        if (kind == "diagnostic") { options = options with { MaxBedrockStreamDiagnostics = 1 }; var first = BedrockStreamWire.Payload(rows[0], nativeNova); first["a"] = null; first["b"] = false; }
        var body = new OllamaBodyStream(BedrockStreamWire.Bytes(nativeNova, rows)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body)));
        var native = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" });
        var provider = new ConvertingBedrockLlmProvider(native, nativeNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, "explicit", options);
        await using var client = Client(provider, publicNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, true); List<LlmJsonEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, publicNova, seen, TestContext.Current.CancellationToken));
        Assert.Equal("bedrock_stream_" + kind + "_limit", Assert.Single(failure.Diagnostics).Code); NoTerminal(seen); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>显式资源策略进入完整目标身份，已绑定 Session 不能在第二次调用偷偷换策略。</summary>
    [Theory] [InlineData("block")] [InlineData("argument")] [InlineData("tail")] [InlineData("diagnostic")]
    public async Task ChangedPolicy_RejectsBeforeSigningOrHttp(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(BedrockStreamWire.Bytes(false))));
        var clock = new AwsClock(); var source = new AwsCredentialSource(clock); var factory = new AwsHttpFactory(handler);
        var native = new AwsLlmProvider(factory, source, new() { Region = "us-east-1" }, timeProvider: clock); var store = AwsFixture.Store(); var options = Options();
        await using (var first = Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options), LlmProtocol.BedrockNova, store: store))
            await Collect(first, true, [], TestContext.Current.CancellationToken);
        options = kind switch { "block" => options with { MaxBedrockStreamBlocks = 129 }, "argument" => options with { MaxBedrockStreamToolArgumentBytes = 1024 * 1024 + 1 },
            "tail" => options with { MaxBedrockStreamTailBytes = 1024 * 1024 + 1 }, _ => options with { MaxBedrockStreamDiagnostics = 1025 } };
        await using var second = Client(new ConvertingBedrockLlmProvider(native, LlmProtocol.BedrockConverse, "explicit", options), LlmProtocol.BedrockNova, store: store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(second, true, [], TestContext.Current.CancellationToken));
        Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls); Assert.Single(factory.Clients);
    }

    /// <summary>默认省略流策略后的精确身份与上一个完整 JSON 版本一致，不无谓失效老 Session。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public void DefaultPolicy_PreservesPreStreamBindingBytes(bool nova)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send"));
        var native = new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }); var protocol = nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse;
        var provider = new ConvertingBedrockLlmProvider(native, protocol, "explicit", Options());
        var expected = typeof(ConvertingBedrockLlmProvider).AssemblyQualifiedName + ":bedrock-json-conversion-v1:" + native.Id + ":" + native.BindingIdentity + ":" + protocol + ":" + BedrockStreamWire.Read("PreStreamOptions").ToJsonString();
        Assert.Equal(expected, provider.BindingIdentity); Assert.Equal(0, handler.Calls);
    }
}
