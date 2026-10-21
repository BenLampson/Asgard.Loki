using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Cloudflare.Tests.CloudflareFixture;

namespace Asgard.Loki.LLM.Cloudflare.Tests;

/// <summary>原生模型路径/认证/不可变快照及完整实际目标，始终不连接 Cloudflare。</summary>
public sealed class CloudflareBindingTests
{
    /// <summary>未绑定的认证或 Gateway 默认头在派发前失败，不能改变明确选定的原生服务。</summary>
    [Theory] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")] [InlineData("cf-aig-gateway-id")] [InlineData("cf-aig-authorization")]
    public async Task UnboundDefaults_AreRejected(string name)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CloudflareHttpFactory(handler)
        { Configure = client => client.DefaultRequestHeaders.TryAddWithoutValidation(name, "fixture-default") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(new(factory), true)); Assert.Equal(0, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>匿名与 External 身份不能偷偷继承客户端默认令牌。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedCredential_FailsBeforeFactory(bool external)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CloudflareHttpFactory(handler); var provider = new CloudflareTextProvider(factory);
        var credential = external ? LlmCredential.External("workers-key", "principal") : LlmCredential.Anonymous("workers-key");
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteAsync(Payload(), Context(credential: credential), TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
        Assert.True(provider.SupportsCredential(LlmCredentialKind.ApiKey)); Assert.False(provider.SupportsCredential(credential.Kind));
    }
    /// <summary>坏原生输入、控制字段、UTF-16、根地址或点路径在任何工厂/等待前失败。</summary>
    [Theory] [InlineData("input-missing")] [InlineData("prompt-null")] [InlineData("messages-scalar")] [InlineData("messages-item")]
    [InlineData("model")] [InlineData("MODEL")] [InlineData("STREAM")]
    [InlineData("unicode-value")] [InlineData("unicode-key")] [InlineData("unicode-element")] [InlineData("duplicate")] [InlineData("unicode-model")]
    [InlineData("model-dot")] [InlineData("model-parent")] [InlineData("model-empty-segment")]
    [InlineData("root-query")] [InlineData("root-fragment")] [InlineData("root-user")] [InlineData("root-relative")]
    public async Task InvalidDirectInput_DoesNotCreateClient(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CloudflareHttpFactory(handler); var provider = new CloudflareTextProvider(factory);
        var payload = Payload(); var target = Target(); var invalid = new string((char)0xd800, 1);
        switch (kind)
        {
            case "input-missing": payload = []; break; case "prompt-null": payload = new() { ["prompt"] = null }; break;
            case "messages-scalar": payload = new() { ["messages"] = "text" }; break; case "messages-item": payload = new() { ["messages"] = new JsonArray(1) }; break;
            case "model" or "MODEL" or "STREAM": payload[kind] = false; break;
            case "unicode-value": payload["future"] = invalid; break; case "unicode-key": payload[invalid] = null; break;
            case "unicode-element": payload["future"] = JsonNode.Parse("{\"x\":\"\\ud800\"}"); break;
            case "duplicate": payload = JsonNode.Parse("{\"prompt\":\"text\",\"future\":{\"x\":1,\"x\":2}}")!.AsObject(); break;
            case "unicode-model": target = target with { UpstreamModel = invalid }; break;
            case "model-dot": target = target with { UpstreamModel = "@cf/./model" }; break; case "model-parent": target = target with { UpstreamModel = "@cf/../model" }; break;
            case "model-empty-segment": target = target with { UpstreamModel = "@cf//model" }; break;
            case "root-query": target = target with { BaseAddress = new("https://native.invalid/ai/run/?token=fixture") }; break;
            case "root-fragment": target = target with { BaseAddress = new("https://native.invalid/ai/run/#fragment") }; break;
            case "root-user": target = target with { BaseAddress = new("https://fixture@native.invalid/ai/run/") }; break;
            case "root-relative": target = target with { BaseAddress = new("ai/run/", UriKind.Relative) }; break;
        }
        Assert.NotNull(await Xunit.Record.ExceptionAsync(() => provider.CompleteAsync(payload, Context(target), TestContext.Current.CancellationToken))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>直接适配器调用也在首次工厂创建前复制原生请求，不借用调用者可变容器。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DirectSnapshot_PrecedesFactoryCreation(bool stream)
    {
        var payload = Payload(); using var handler = new StubHttpHandler(async (message, token) =>
        {
            var actual = JsonSerializer.Deserialize<JsonElement>(await message.Content!.ReadAsByteArrayAsync(token)); Assert.True(JsonElement.DeepEquals(Part(stream ? "wire_stream" : "wire_complete"), actual));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream ? Frame(NativeRecord) + Done : Complete, Encoding.UTF8, stream ? "text/event-stream" : "application/json") }; return response;
        });
        using var factory = new CloudflareHttpFactory(handler) { Configure = _ => payload.Clear() }; var provider = new CloudflareTextProvider(factory);
        if (stream) await foreach (var _ in provider.StreamAsync(payload, Context(), TestContext.Current.CancellationToken)) { } else await provider.CompleteAsync(payload, Context(), TestContext.Current.CancellationToken);
        Assert.Empty(payload); Assert.Equal(1, handler.Calls);
    }
    /// <summary>本地字节策略及命名客户端变化都是完整实现漂移，已绑定 Session 不能静默改用。</summary>
    [Theory] [InlineData("request")] [InlineData("response")] [InlineData("stream")] [InlineData("client")]
    public async Task PolicyDrift_RejectsBoundSessionBeforeSend(string kind)
    {
        using var handler = Reply(new CloudflareBodyStream(Encoding.UTF8.GetBytes(Complete))); using var factory = new CloudflareHttpFactory(handler); var provider = new CloudflareTextProvider(factory); var store = Store();
        await using (var first = Client(provider, store)) await first.LLM.WorkersAiText.CompleteAsync(Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken);
        var options = kind switch { "request" => new CloudflareTextOptions { MaximumRequestBytes = 20000 }, "response" => new CloudflareTextOptions { MaximumResponseBytes = 20000 }, "stream" => new CloudflareTextOptions { MaximumStreamBytes = 20000 }, _ => new CloudflareTextOptions() };
        var changed = new CloudflareTextProvider(factory, httpClientName: kind == "client" ? "changed-client" : "Asgard.Loki.LLM.Cloudflare", options: options); Assert.NotEqual(provider.BindingIdentity, changed.BindingIdentity);
        await using var next = Client(changed, store); await Assert.ThrowsAsync<InvalidOperationException>(() => next.LLM.WorkersAiText.CompleteAsync(Request(), new() { SessionId = "bound" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>实际 HTTP 方法/目标在派发、EOF 或客户端释放时漂移都会阻止原生完成。</summary>
    [Theory] [InlineData(false, "dispatch-uri")] [InlineData(true, "dispatch-uri")] [InlineData(false, "dispatch-method")] [InlineData(true, "dispatch-method")]
    [InlineData(false, "response-uri")] [InlineData(true, "response-uri")] [InlineData(false, "eof-uri")] [InlineData(true, "eof-uri")]
    [InlineData(false, "dispose-uri")] [InlineData(true, "dispose-uri")]
    public async Task ActualTargetMutation_PreventsPublication(bool stream, string kind)
    {
        HttpRequestMessage? sent = null; var body = new CloudflareBodyStream(Encoding.UTF8.GetBytes(stream ? Frame(NativeRecord) + Done : Complete))
        { OnEof = kind == "eof-uri" ? () => sent!.RequestUri = new("https://other.invalid/run") : null };
        using var handler = new StubHttpHandler((message, _) =>
        {
            sent = message; if (kind == "dispatch-uri") message.RequestUri = new("https://other.invalid/run"); if (kind == "dispatch-method") message.Method = HttpMethod.Get;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json");
            if (kind == "response-uri") response.RequestMessage = new(HttpMethod.Post, "https://other.invalid/run"); return Task.FromResult(response);
        });
        using var factory = new CloudflareHttpFactory(handler) { OnClientDispose = kind == "dispose-uri" ? () => sent!.RequestUri = new("https://other.invalid/run") : null }; var provider = new CloudflareTextProvider(factory); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (stream) await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; else await provider.CompleteAsync(Payload(), Context(), TestContext.Current.CancellationToken); });
        Assert.False(terminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>所有资源预算必须为正，构造阶段不创建客户端。</summary>
    [Theory] [InlineData("request", 0)] [InlineData("request", -1)] [InlineData("response", 0)] [InlineData("response", -1)] [InlineData("stream", 0)] [InlineData("stream", -1)]
    public void InvalidBudget_IsRejected(string kind, int value)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CloudflareHttpFactory(handler);
        var options = kind switch { "request" => new CloudflareTextOptions { MaximumRequestBytes = value }, "response" => new CloudflareTextOptions { MaximumResponseBytes = value }, _ => new CloudflareTextOptions { MaximumStreamBytes = value } };
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudflareTextProvider(factory, options: options)); Assert.Empty(factory.Clients);
    }
    /// <summary>非法原始实现身份不能经 JSON 指纹被静默替换后绑定到另一配置。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void InvalidProviderIdentity_IsRejected(bool client)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CloudflareHttpFactory(handler); var invalid = new string((char)0xd800, 1);
        Assert.Throws<EncoderFallbackException>(() => new CloudflareTextProvider(factory, id: client ? "cloudflare-text" : invalid, httpClientName: client ? invalid : "native-client")); Assert.Empty(factory.Clients);
    }
}
