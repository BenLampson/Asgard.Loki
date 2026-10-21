using System.Net;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.AWS.Tests.BedrockConversionFixture;
using static Asgard.Loki.LLM.AWS.Tests.BedrockStreamProviderTests;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>真实原生二进制适配器的迟到故障、资源边界和上下文错误不能泄漏转换成功。</summary>
public sealed class BedrockStreamFailureTests
{
    /// <summary>每种源失败覆盖两个上游原生 API，生成请求只执行一次。</summary>
    public static IEnumerable<object[]> Faults()
    {
        foreach (var nova in new[] { false, true }) foreach (var kind in new[] { "read", "body-dispose", "client-dispose", "eof-cancel", "dispose-cancel", "late-error", "partial-frame", "bad-crc", "no-stop", "no-usage", "missing-metrics" }) yield return [nova, kind];
    }
    /// <summary>完整原生停止之后读/释放/CRC/错误/取消仍会失败，转换终态一律保持未交付。</summary>
    [Theory] [MemberData(nameof(Faults))]
    public async Task LateNativeFault_PreventsConvertedTerminal(bool publicNova, string kind)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var nativeNova = !publicNova; var rows = BedrockStreamWire.Rows(nativeNova);
        if (kind == "no-stop") rows.RemoveAt(13); if (kind == "no-usage") rows.RemoveAt(14);
        if (kind == "missing-metrics") BedrockStreamWire.Payload(rows[14], nativeNova).Remove("metrics");
        var bytes = BedrockStreamWire.Bytes(nativeNova, rows); const string error = "{\"message\":\"late-native-error\"}";
        if (kind == "late-error") bytes = [.. bytes, .. StreamFixture.Frame("exception", "modelStreamErrorException", error)];
        if (kind == "partial-frame") bytes = [.. bytes, 0, 1]; if (kind == "bad-crc") bytes[^1] ^= 1;
        var body = new OllamaBodyStream(bytes) { FailAtEof = kind == "read", FailDispose = kind == "body-dispose", OnEof = kind == "eof-cancel" ? cancellation.Cancel : null, OnDispose = kind == "dispose-cancel" ? cancellation.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body)));
        var factory = new AwsHttpFactory(handler) { OnDispose = kind == "client-dispose" ? () => throw new IOException("client dispose") : null };
        var native = new AwsLlmProvider(factory, new AwsLlmOptions { Region = "us-east-1" });
        var provider = new ConvertingBedrockLlmProvider(native, nativeNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, "explicit", Options() with { AllowLoss = true });
        await using var client = Client(provider, publicNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, true); List<LlmJsonEvent> seen = [];
        var failure = await Record.ExceptionAsync(() => Collect(client, publicNova, seen, cancellation.Token));
        if (kind.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else if (kind is "read" or "body-dispose" or "client-dispose") Assert.IsType<IOException>(failure);
        else if (kind == "late-error") { var upstream = Assert.IsType<LlmUpstreamException>(failure); Assert.Equal(HttpStatusCode.OK, upstream.StatusCode); Assert.Equal(error, upstream.ResponseBody); Assert.NotNull(upstream.GetAwsEventStreamFrame()); }
        else Assert.IsType<InvalidDataException>(failure);
        Assert.NotEmpty(seen); NoTerminal(seen); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>跨请求历史 ID 复用在新源工具开始时立即失败，不改写原始 ID 或等到工具回填。</summary>
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task NativeCallReusingHistoricalId_RejectsPublicStream(bool publicNova)
    {
        var nativeNova = !publicNova; var rows = BedrockStreamWire.Rows(nativeNova); BedrockStreamWire.Payload(rows[4], nativeNova)["start"]!["toolUse"]!["toolUseId"] = "call-a";
        var body = new OllamaBodyStream(BedrockStreamWire.Bytes(nativeNova, rows)); using var handler = new StubHttpHandler((_, _) => Task.FromResult(StreamFixture.Reply(body)));
        var provider = new ConvertingBedrockLlmProvider(new AwsLlmProvider(new AwsHttpFactory(handler), new AwsLlmOptions { Region = "us-east-1" }), nativeNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, "explicit", Options() with { AllowLoss = true });
        await using var client = Client(provider, publicNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, true); List<LlmJsonEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmConversionException>(() => Collect(client, publicNova, seen, TestContext.Current.CancellationToken));
        Assert.Equal("tool_call_id_reused", Assert.Single(failure.Diagnostics).Code); NoTerminal(seen); Assert.True(body.Disposed); Assert.Equal(1, handler.Calls);
    }
}
