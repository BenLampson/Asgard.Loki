using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Conversion.Tests.GenerationSettingsTests;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>每个转换方向的公开 SDK 都在 HTTP 前执行支持声明，不修改请求或偷偷重试。</summary>
public sealed class TargetSettingsPipelineTests
{
    /// <summary>十二个方向均覆盖完整调用和流式枚举。</summary>
    public static TheoryData<LlmProtocol, LlmProtocol, bool> Calls => ConvertingProviderTests.Calls;

    /// <summary>允许的强度到达原生字段；原始可变列表在注册后修改也不能改变行为。</summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task AllowedSettings_ArriveAtWireWithStableSnapshot(LlmProtocol source, LlmProtocol upstream, bool stream)
    {
        List<string> allowed = ["medium"];
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
            Assert.Equal("medium", Effort(body, upstream).ToLowerInvariant());
            if (!stream) return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ConversionFixtures.Response(upstream).ToJsonString(), Encoding.UTF8, "application/json") };
            var events = string.Concat(ConversionFixtures.Stream(upstream).Select(item => "data: " + item.ToJsonString() + "\n\n"))
                + (upstream == LlmProtocol.ChatCompletions ? "data: [DONE]\n\n" : "");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        });
        await using var client = ConvertingProviderTests.Build(handler, source, upstream, options: new()
        {
            DefaultMaxTokens = 4096,
            TargetSettings = new() { AllowedReasoningEfforts = allowed, MaximumOutputTokens = 4096, SupportsSampling = false }
        });
        allowed[0] = "low";
        var input = Request(source); input["model"] = "public"; SetEffort(input, source, "medium"); var before = input.ToJsonString();
        if (stream) Assert.NotEmpty(await ConvertingProviderTests.StreamAsync(client, source, input));
        else Assert.NotNull((await ConvertingProviderTests.CompleteAsync(client, source, input)).Metadata.Conversion);
        Assert.Equal(before, input.ToJsonString()); Assert.Equal(1, handler.Calls);
    }

    /// <summary>不支持的最终强度在 HTTP 前硬失败，完整与流式都不能被 AllowLoss 放行。</summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public async Task DeniedSettings_NeverCallUpstream(LlmProtocol source, LlmProtocol upstream, bool stream)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("不应调用上游。"));
        await using var client = ConvertingProviderTests.Build(handler, source, upstream, options: new()
        { DefaultMaxTokens = 4096, AllowLoss = true, TargetSettings = new() { AllowedReasoningEfforts = ["low"] } });
        var input = Request(source); input["model"] = "public"; SetEffort(input, source, "medium");
        if (stream) await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.StreamAsync(client, source, input));
        else await Assert.ThrowsAsync<LlmConversionException>(() => ConvertingProviderTests.CompleteAsync(client, source, input));
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>同协议装饰器仍走完整原生调用，不把转换声明变成全局网关参数过滤。</summary>
    [Fact]
    public async Task NativeBranch_DoesNotApplyConversionRestrictions()
    {
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            Assert.Equal(0, body["temperature"]!.GetValue<int>());
            Assert.Equal(4096, body["max_completion_tokens"]!.GetValue<int>());
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ConversionFixtures.Response(LlmProtocol.ChatCompletions).ToJsonString(), Encoding.UTF8, "application/json") };
        });
        await using var client = ConvertingProviderTests.Build(handler, LlmProtocol.ChatCompletions, LlmProtocol.ChatCompletions,
            options: new() { TargetSettings = new() { SupportsSampling = false, MaximumOutputTokens = 1 } });
        var input = Request(LlmProtocol.ChatCompletions); input["model"] = "public"; input["temperature"] = 0;
        Assert.Null((await ConvertingProviderTests.CompleteAsync(client, LlmProtocol.ChatCompletions, input)).Metadata.Conversion);
        Assert.Equal(1, handler.Calls);
    }
}
