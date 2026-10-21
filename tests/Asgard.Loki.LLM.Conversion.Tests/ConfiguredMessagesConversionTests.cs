using System.Net;
using System.Text;
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

/// <summary>显式转换装饰器透传原生 HTTP 配置和凭据支持，不承担认证探测或动态路由。</summary>
public sealed class ConfiguredMessagesConversionTests
{
    /// <summary>三个公开协议分别使用固定 Messages 上游，覆盖五种认证和完整/流式调用。</summary>
    public static IEnumerable<object[]> Cases()
    { foreach (var source in new[] { LlmProtocol.Responses, LlmProtocol.ChatCompletions, LlmProtocol.Gemini }) foreach (var auth in Enum.GetValues<AnthropicAuthentication>()) foreach (var stream in new[] { false, true }) yield return [source, auth, stream]; }
    /// <summary>选中地址、凭据位置、实际模型、原生工具及上游窗口贯穿真实 SDK 转换流水线。</summary>
    [Theory] [MemberData(nameof(Cases))]
    public async Task PublicConversion_UsesExplicitNativeTransport(LlmProtocol source, AnthropicAuthentication auth, bool stream)
    {
        using var handler = new StubHttpHandler(async (message, token) =>
        {
            Assert.Equal("https://configured.invalid/api/custom/messages?existing=1"+(auth == AnthropicAuthentication.Query ? "&api_key=bound%2Bkey%2F" : ""), message.RequestUri!.OriginalString); Assert.Equal("configured.invalid", message.Headers.Host);
            if (auth == AnthropicAuthentication.Bearer) Assert.Equal("bound+key/", message.Headers.Authorization!.Parameter); else Assert.False(message.Headers.Contains("Authorization"));
            if (auth == AnthropicAuthentication.ApiKey) Assert.Equal("bound+key/", Assert.Single(message.Headers.GetValues("x-api-key"))); else Assert.False(message.Headers.Contains("x-api-key"));
            Assert.Equal("2023-06-01", Assert.Single(message.Headers.GetValues("anthropic-version")));
            if (auth == AnthropicAuthentication.Header) Assert.Equal("bound+key/", Assert.Single(message.Headers.GetValues("x-native-key"))); else Assert.False(message.Headers.Contains("x-native-key"));
            var body = JsonNode.Parse(await message.Content!.ReadAsByteArrayAsync(token))!; Assert.Equal("model-a", body["model"]!.GetValue<string>()); Assert.Equal(stream, body["stream"]!.GetValue<bool>()); Assert.Contains("call_1", body.ToJsonString()); Assert.Contains("lookup", body.ToJsonString()); Assert.DoesNotContain("bound+key/", body.ToJsonString());
            return Reply(stream);
        }); await using var client = Build(handler, source, auth, Store()); var request = ConversionFixtures.Request(source); request["model"] = "public"; var before = request.ToJsonString();
        if (stream)
        {
            var events = await ConvertingProviderTests.StreamAsync(client, source, request); Assert.NotEmpty(events); Assert.All(events, item => { Assert.Equal(LlmProtocol.ClaudeMessages, item.Metadata.Conversion!.UpstreamProtocol); Assert.Empty(item.Metadata.Conversion.Diagnostics); }); Assert.Contains(events, item => item.Data.GetRawText().Contains("lookup", StringComparison.Ordinal));
        }
        else
        {
            var result = await ConvertingProviderTests.CompleteAsync(client, source, request); Assert.Equal(source, result.Metadata.Conversion!.PublicProtocol); Assert.Equal(LlmProtocol.ClaudeMessages, result.Metadata.Conversion.UpstreamProtocol); Assert.Empty(result.Metadata.Conversion.Diagnostics); Assert.True(JsonNode.DeepEquals(ConversionFixtures.Response(LlmProtocol.ClaudeMessages), JsonNode.Parse(result.Metadata.Conversion.UpstreamData.GetRawText()))); Assert.Contains("lookup", result.Data.GetRawText());
        }
        Assert.Equal(before, request.ToJsonString()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>装饰器身份包含明确操作路径，五种认证都不能复用更换端点后的 Session。</summary>
    [Theory] [InlineData(AnthropicAuthentication.ApiKey)] [InlineData(AnthropicAuthentication.Bearer)] [InlineData(AnthropicAuthentication.None)] [InlineData(AnthropicAuthentication.Header)] [InlineData(AnthropicAuthentication.Query)]
    public async Task NativeEndpointChange_InvalidatesConvertedSession(AnthropicAuthentication auth)
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(false))); var store = Store(); var request = ConversionFixtures.Request(LlmProtocol.Responses); request["model"] = "public";
        await using (var first = Build(handler, LlmProtocol.Responses, auth, store)) await ConvertingProviderTests.CompleteAsync(first, LlmProtocol.Responses, request);
        await using var changed = Build(handler, LlmProtocol.Responses, auth, store, "changed/messages"); await Assert.ThrowsAsync<InvalidOperationException>(() => ConvertingProviderTests.CompleteAsync(changed, LlmProtocol.Responses, request)); Assert.Equal(1, handler.Calls);
    }
    private static HttpResponseMessage Reply(bool stream)
    {
        if (!stream) return new(HttpStatusCode.OK) { Content = new StringContent(ConversionFixtures.Response(LlmProtocol.ClaudeMessages).ToJsonString(), Encoding.UTF8, "application/json") };
        var text = string.Concat(ConversionFixtures.Stream(LlmProtocol.ClaudeMessages).Select(item => "data: "+item.ToJsonString()+"\n\n")); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FragmentedReadStream(text)) }; response.Content.Headers.ContentType = new("text/event-stream"); return response;
    }
    private static ILokiClient Build(HttpMessageHandler handler, LlmProtocol source, AnthropicAuthentication auth, ILlmSessionStore store, string path = "custom/messages?existing=1")
    {
        var native = new AnthropicLlmProvider(new StubHttpClientFactory(handler), new AnthropicHttpOptions { Authentication = auth, Transport = new() { MessagesPath = path, CredentialName = auth switch { AnthropicAuthentication.Header => "x-native-key", AnthropicAuthentication.Query => "api_key", _ => null } } }); var provider = new ConvertingLlmProvider(native, LlmProtocol.ClaudeMessages, "configured-conversion");
        return new LokiClientBuilder().AddLLM(new() { Providers = [provider], SessionStore = store, Credentials = [auth == AnthropicAuthentication.None ? LlmCredential.Anonymous("credential") : new("credential", "bound+key/")], Routes = [new() { Model = "public", Targets = [new() { Id = "fixed", ProviderId = provider.Id, BaseAddress = new("https://configured.invalid/api/"), UpstreamModel = "model-a", CredentialId = "credential", Protocols = source }] }] }).Build();
    }
    private static ILlmSessionStore Store() => new DistributedCacheLlmSessionStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
}
