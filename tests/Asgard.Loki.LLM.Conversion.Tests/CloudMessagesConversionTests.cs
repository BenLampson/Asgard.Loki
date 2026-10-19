using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Anthropic;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>固定云 Messages 目标上的显式推理/Schema 转换与认证委托，不查询或执行模型。</summary>
public sealed class CloudMessagesConversionTests
{
    /// <summary>单块最终数组的索引必须是零；严格转换不把漏块源当作合法完成。</summary>
    [Theory] [InlineData(7)] [InlineData(int.MaxValue)]
    public async Task SparseContentArrayIndex_DoesNotBecomeConvertedSuccess(int index)
    {
        var events = Golden().GetProperty("nativeEvents").EnumerateArray().Select(item => JsonNode.Parse(item.GetRawText())!.AsObject()).ToArray();
        foreach (var item in events) if (item.ContainsKey("index")) item["index"] = index;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(string.Concat(events.Select(item => "data: " + item.ToJsonString() + "\n\n")), true)));
        await using var client = Build(handler, false, true); List<ChatCompletionEvent> seen = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var item in client.LLM.ChatCompletions.StreamAsync(Golden().GetProperty("sourceHigh").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "session" }, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.DoesNotContain(seen, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)); Assert.Equal(1, handler.Calls);
    }

    /// <summary>显式认证、推理模式与方法分别独立配置。</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var bearer in new[] { false, true }) foreach (var disabled in new[] { false, true }) foreach (var stream in new[] { false, true }) yield return [bearer, disabled, stream];
    }
    /// <summary>完整最终 HTTP 匹配独立报文，Schema 精度和模式不被认证选项改变。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task ReasoningAndSchema_ReachExplicitNativeTarget(bool bearer, bool disabled, bool stream)
    {
        var golden = Golden(); var request = JsonNode.Parse(golden.GetProperty(disabled ? "sourceNone" : "sourceHigh").GetRawText())!.AsObject(); var before = request.ToJsonString();
        var target = JsonNode.Parse(golden.GetProperty(disabled ? "targetNone" : "targetHigh").GetRawText())!.AsObject(); target["stream"] = stream;
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/messages", message.RequestUri!.AbsoluteUri);
            if (bearer) { Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("bound-key", message.Headers.Authorization.Parameter); Assert.False(message.Headers.Contains("x-api-key")); }
            else { Assert.Null(message.Headers.Authorization); Assert.Equal("bound-key", Assert.Single(message.Headers.GetValues("x-api-key"))); }
            var actual = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token)); Assert.True(JsonNode.DeepEquals(target, actual));
            Assert.Equal("9007199254740993", actual!["output_config"]!["format"]!["schema"]!["properties"]!["n"]!["minimum"]!.ToJsonString());
            Assert.Equal(4096, actual["max_tokens"]!.GetValue<int>()); Assert.False(actual["thinking"]!.AsObject().ContainsKey("budget_tokens"));
            return Reply(stream ? golden.GetProperty("nativeWire").GetString()! : golden.GetProperty("nativeComplete").GetRawText(), stream);
        });
        await using var client = Build(handler, bearer, true); var response = new List<LlmJsonResult>();
        if (stream) await foreach (var item in client.LLM.ChatCompletions.StreamAsync(request.Deserialize<ChatCompletionRequest>()!, new() { SessionId = "session" }, TestContext.Current.CancellationToken)) response.Add(item);
        else response.Add(await client.LLM.ChatCompletions.CompleteAsync(request.Deserialize<ChatCompletionRequest>()!, new() { SessionId = "session" }, TestContext.Current.CancellationToken));
        Assert.NotEmpty(response); Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
        Assert.All(response, item =>
        {
            var conversion = Assert.IsType<LlmConversionInfo>(item.Metadata.Conversion); Assert.Equal(LlmProtocol.ClaudeMessages, conversion.UpstreamProtocol);
            Assert.Equal(LlmProtocol.ChatCompletions, conversion.PublicProtocol); Assert.Equal("cloud-model", item.Metadata.UpstreamModel);
            Assert.Equal("format_metadata_not_representable", Assert.Single(conversion.Diagnostics).Code);
        });
        var final = response[^1].Data; Assert.Equal("stop", final.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Contains(response, item => item.Data.GetRawText().Contains("9007199254740993", StringComparison.Ordinal));
        if (!stream) Assert.True(JsonElement.DeepEquals(golden.GetProperty("nativeComplete"), response[0].Metadata.Conversion!.UpstreamData));
    }
    /// <summary>格式名无法表达时仍需显式损耗许可；认证不能绕过转换边界。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MissingLossConsent_RejectsBeforeHttp(bool bearer)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); await using var client = Build(handler, bearer, false);
        var request = Golden().GetProperty("sourceHigh").Deserialize<ChatCompletionRequest>()!;
        var failure = await Assert.ThrowsAsync<LlmConversionException>(() => client.LLM.ChatCompletions.CompleteAsync(request, new() { SessionId = "session" }, TestContext.Current.CancellationToken));
        Assert.Equal("format_metadata_not_representable", Assert.Single(failure.Diagnostics).Code); Assert.Equal(0, handler.Calls);
    }
    /// <summary>上游原生完成后的迟到错误不能发布目标成功终态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LateNativeError_HasNoConvertedSuccess(bool bearer)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"future\":false}}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(Golden().GetProperty("nativeWire").GetString()! + "data: " + error + "\n\n", true)));
        await using var client = Build(handler, bearer, true); List<ChatCompletionEvent> seen = [];
        var failure = await Assert.ThrowsAsync<LlmUpstreamException>(async () => { await foreach (var item in client.LLM.ChatCompletions.StreamAsync(Golden().GetProperty("sourceHigh").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "session" }, TestContext.Current.CancellationToken)) seen.Add(item); });
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode); Assert.Equal(error, failure.ResponseBody); Assert.NotEmpty(seen);
        Assert.DoesNotContain(seen, item => item.Data.GetProperty("choices").EnumerateArray().Any(choice => choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>手动预算不能覆盖来源明确关闭的模式，即使允许其他损耗。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ExplicitBudget_CannotOverrideDisabledMode(bool bearer)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); await using var client = Build(handler, bearer, true, 1024);
        await Assert.ThrowsAsync<ArgumentException>(() => client.LLM.ChatCompletions.CompleteAsync(Golden().GetProperty("sourceNone").Deserialize<ChatCompletionRequest>()!, new() { SessionId = "session" }, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>生产 Conversion 仍只引用 LLM，测试组合两个独立供应商对象。</summary>
    private static ILokiClient Build(StubHttpHandler handler, bool bearer, bool allowLoss, int? budget = null)
    {
        var native = new AnthropicLlmProvider(new StubHttpClientFactory(handler), new AnthropicHttpOptions { Authentication = bearer ? AnthropicAuthentication.Bearer : AnthropicAuthentication.ApiKey });
        var provider = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "explicit", new() { AllowLoss = allowLoss, TargetThinkingBudgetTokens = budget });
        return new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], Credentials = [new("key", "bound-key")],
            SessionStore = new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock()),
            Routes = [new LlmRoute { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/"), UpstreamModel = "cloud-model", CredentialId = "key", Protocols = LlmProtocol.ChatCompletions }] }] }).Build();
    }
    /// <summary>完全独立的数据清单，不运行转换器来建立预期。</summary>
    private static JsonElement Golden()
    {
        using var source = typeof(CloudMessagesConversionTests).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.Conversion.Tests.Fixtures.cloud-messages-conversion.json")!; using var document = JsonDocument.Parse(source); return document.RootElement.Clone();
    }
    /// <summary>所有 HTTP 均由本地处理器替代。</summary>
    private static HttpResponseMessage Reply(string text, bool stream)
    {
        var content = new StreamContent(new FragmentedReadStream(text)); content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); return new(HttpStatusCode.OK) { Content = content };
    }
}
