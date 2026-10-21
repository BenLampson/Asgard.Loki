using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.Cohere.Tests.CohereFixture;

namespace Asgard.Loki.LLM.Cohere.Tests;

/// <summary>显式认证、直接调用快照、配置指纹及完整实际 HTTP 目标契约。</summary>
public sealed class CohereBindingTests
{
    /// <summary>工厂默认项目、SSE 或另一认证方式不能成为未绑定的实际请求。</summary>
    [Theory] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")] [InlineData("Accepts")] [InlineData("X-Client-Name")]
    public async Task UnboundDefaults_AreRejected(string name)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CohereHttpFactory(handler)
        { Configure = client => client.DefaultRequestHeaders.TryAddWithoutValidation(name, name == "Accepts" ? "text/event-stream" : "fixture-default") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => CohereTransportTests.Invoke(new(factory), true)); Assert.Equal(0, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>不支持的凭据类型在创建客户端前失败，不能借用 Rerank 或默认 Key。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnsupportedCredentials_AreRejectedBeforeFactory(bool external)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CohereHttpFactory(handler); var provider = new CohereLlmProvider(factory);
        var credential = external ? LlmCredential.External("key", "principal") : LlmCredential.Anonymous("key");
        await Assert.ThrowsAsync<ArgumentException>(() => provider.CompleteAsync(Payload(), Context(credential: credential), TestContext.Current.CancellationToken)); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
        Assert.True(provider.SupportsCredential(LlmCredentialKind.ApiKey)); Assert.False(provider.SupportsCredential(credential.Kind));
    }
    /// <summary>非法原生请求、根地址和原始 Unicode 先失败，不创建 HTTP 资源或被序列化器修复。</summary>
    [Theory] [InlineData("message-missing")] [InlineData("message-null")] [InlineData("message-number")] [InlineData("model-alias")] [InlineData("stream-alias")]
    [InlineData("unicode-value")] [InlineData("unicode-key")] [InlineData("unicode-element")] [InlineData("duplicate")] [InlineData("unicode-model")]
    [InlineData("root-query")] [InlineData("root-fragment")] [InlineData("root-user")] [InlineData("root-relative")]
    public async Task InvalidDirectInput_FailsBeforeFactory(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CohereHttpFactory(handler); var provider = new CohereLlmProvider(factory);
        var payload = Payload(); var target = Target(); var invalid = new string((char)0xd800, 1);
        switch (kind)
        {
            case "message-missing": payload.Remove("message"); break; case "message-null": payload["message"] = null; break; case "message-number": payload["message"] = 0; break;
            case "model-alias": payload["MODEL"] = "unselected"; break; case "stream-alias": payload["STREAM"] = false; break;
            case "unicode-value": payload["future"] = invalid; break; case "unicode-key": payload[invalid] = null; break;
            case "unicode-element": payload["future"] = JsonNode.Parse("{\"unknown\":\"\\ud800\"}"); break;
            case "duplicate": payload = JsonNode.Parse("{\"message\":\"x\",\"future\":{\"x\":1,\"x\":2}}")!.AsObject(); break;
            case "unicode-model": target = target with { UpstreamModel = invalid }; break;
            case "root-query": target = target with { BaseAddress = new("https://native.invalid/v1/?key=fixture") }; break;
            case "root-fragment": target = target with { BaseAddress = new("https://native.invalid/v1/#fragment") }; break;
            case "root-user": target = target with { BaseAddress = new("https://fixture@native.invalid/v1/") }; break;
            case "root-relative": target = target with { BaseAddress = new("v1/", UriKind.Relative) }; break;
        }
        Assert.NotNull(await Record.ExceptionAsync(() => provider.CompleteAsync(payload, Context(target), TestContext.Current.CancellationToken))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>直接适配器调用也在首次工厂创建前冻结输入，调用者修改不会改变已生成请求字节。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DirectSnapshot_PrecedesFactoryAndAwait(bool stream)
    {
        var payload = Payload(); using var handler = new StubHttpHandler(async (request, token) =>
        {
            var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject(); var expected = JsonNode.Parse(Text("wire.json"))!.AsObject(); expected["stream"] = stream;
            Assert.True(JsonNode.DeepEquals(expected, actual)); return new(HttpStatusCode.OK) { Content = new StringContent(stream ? Start + "\n" + End : "{\"text\":\"\"}", Encoding.UTF8, "application/json") };
        });
        using var factory = new CohereHttpFactory(handler) { Configure = _ => payload.Clear() }; var provider = new CohereLlmProvider(factory);
        if (stream) await foreach (var _ in provider.StreamAsync(payload, Context(), TestContext.Current.CancellationToken)) { } else await provider.CompleteAsync(payload, Context(), TestContext.Current.CancellationToken);
        Assert.Empty(payload); Assert.Equal(1, handler.Calls);
    }
    /// <summary>所有本地 HTTP/流式策略都是适配实现身份的一部分，旧 Session 不静默采用新策略。</summary>
    [Theory] [InlineData("request")] [InlineData("response")] [InlineData("record")] [InlineData("total")] [InlineData("tail")] [InlineData("project")] [InlineData("client")]
    public async Task ProviderPolicyDrift_RejectsExistingSession(string kind)
    {
        using var handler = CohereNativeTests.Response(new CohereBodyStream(Encoding.UTF8.GetBytes("{\"text\":\"\"}"))); using var factory = new CohereHttpFactory(handler); var store = Store(); var provider = new CohereLlmProvider(factory);
        await using (var first = Client(provider, store)) await first.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken);
        var options = kind switch
        {
            "request" => new CohereLlmOptions { MaximumRequestBytes = 20000 }, "response" => new CohereLlmOptions { MaximumResponseBytes = 20000 },
            "record" => new CohereLlmOptions { MaximumStreamRecordBytes = 20000 }, "total" => new CohereLlmOptions { MaximumStreamBytes = 20000 },
            "tail" => new CohereLlmOptions { MaximumBufferedTailRecords = 5 }, "project" => new CohereLlmOptions { ClientName = "other-project" }, _ => new CohereLlmOptions()
        };
        var changed = new CohereLlmProvider(factory, httpClientName: kind == "client" ? "changed-client" : "Asgard.Loki.LLM.Cohere", options: options); Assert.NotEqual(provider.BindingIdentity, changed.BindingIdentity);
        await using var next = Client(changed, store); await Assert.ThrowsAsync<InvalidOperationException>(() => next.LLM.CohereChat.CompleteAsync(Request(), new() { SessionId = "drift" }, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>实际请求方法或目标在派发、EOF 和客户端释放时改变都会阻止返回完成或流终态。</summary>
    [Theory] [InlineData(false, "dispatch-uri")] [InlineData(true, "dispatch-uri")] [InlineData(false, "dispatch-method")] [InlineData(true, "dispatch-method")]
    [InlineData(false, "response-uri")] [InlineData(true, "response-uri")] [InlineData(false, "eof-uri")] [InlineData(true, "eof-uri")]
    [InlineData(false, "dispose-uri")] [InlineData(true, "dispose-uri")]
    public async Task ActualTargetMutation_PreventsPublication(bool stream, string kind)
    {
        HttpRequestMessage? sent = null; var body = new CohereBodyStream(Encoding.UTF8.GetBytes(stream ? Start + "\n" + End : "{\"text\":\"\"}"))
        { OnEof = kind == "eof-uri" ? () => sent!.RequestUri = new("https://other.invalid/chat") : null };
        using var handler = new StubHttpHandler((request, _) =>
        {
            sent = request; if (kind == "dispatch-uri") request.RequestUri = new("https://other.invalid/chat"); if (kind == "dispatch-method") request.Method = HttpMethod.Get;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }; response.Content.Headers.ContentType = new("application/json");
            if (kind == "response-uri") response.RequestMessage = new(HttpMethod.Post, "https://other.invalid/chat"); return Task.FromResult(response);
        });
        using var factory = new CohereHttpFactory(handler) { OnClientDispose = kind == "dispose-uri" ? () => sent!.RequestUri = new("https://other.invalid/chat") : null };
        var provider = new CohereLlmProvider(factory); var terminal = false;
        await Assert.ThrowsAsync<InvalidDataException>(async () => { if (stream) await foreach (var item in provider.StreamAsync(Payload(), Context(), TestContext.Current.CancellationToken)) terminal |= item.IsTerminal; else await provider.CompleteAsync(Payload(), Context(), TestContext.Current.CancellationToken); });
        Assert.False(terminal); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
    /// <summary>所有字节/尾部策略必须为正，配置阶段不执行 HTTP。</summary>
    [Theory] [InlineData("request", 0)] [InlineData("request", -1)] [InlineData("response", 0)] [InlineData("response", -1)]
    [InlineData("record", 0)] [InlineData("record", -1)] [InlineData("total", 0)] [InlineData("total", -1)] [InlineData("tail", 0)] [InlineData("tail", -1)]
    public void InvalidLimits_AreRejected(string kind, int value)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CohereHttpFactory(handler);
        var options = kind switch
        { "request" => new CohereLlmOptions { MaximumRequestBytes = value }, "response" => new CohereLlmOptions { MaximumResponseBytes = value }, "record" => new CohereLlmOptions { MaximumStreamRecordBytes = value }, "total" => new CohereLlmOptions { MaximumStreamBytes = value }, _ => new CohereLlmOptions { MaximumBufferedTailRecords = value } };
        Assert.Throws<ArgumentOutOfRangeException>(() => new CohereLlmProvider(factory, options: options)); Assert.Empty(factory.Clients);
    }
    /// <summary>配置身份与项目头的非法原始 Unicode 或控制字符不能在指纹中被静默替换。</summary>
    [Theory] [InlineData("id")] [InlineData("client")] [InlineData("project-unicode")] [InlineData("project-control")]
    public void InvalidConfigurationIdentity_IsRejected(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected")); using var factory = new CohereHttpFactory(handler); var invalid = new string((char)0xd800, 1);
        var failure = Record.Exception(() => new CohereLlmProvider(factory, id: kind == "id" ? invalid : "cohere", httpClientName: kind == "client" ? invalid : "named", options: new() { ClientName = kind == "project-unicode" ? invalid : kind == "project-control" ? "x\r\ny" : null }));
        Assert.NotNull(failure); Assert.Empty(factory.Clients);
    }
}
