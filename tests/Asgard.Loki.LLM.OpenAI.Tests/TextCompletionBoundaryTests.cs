using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.TextCompletionFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>原始字段、类型和实际目标边界；失败不能通过参数修补或忽略 native text 来掩盖。</summary>
public sealed class TextCompletionBoundaryTests
{
    /// <summary>原生无效 Unicode、重复嵌入字段、所有权别名和请求预算在工厂前失败。</summary>
    [Theory] [InlineData("unicode")] [InlineData("property")] [InlineData("element-unicode")] [InlineData("custom-container")]
    [InlineData("dom-duplicates")] [InlineData("model-alias")] [InlineData("stream-alias")] [InlineData("budget")] [InlineData("cancel")]
    public async Task OriginalRequestFault_PrecedesFactory(string fault)
    {
        var payload = Payload(); var invalid = new string((char)0xd800, 1);
        switch (fault)
        {
            case "unicode": payload["future"] = invalid; break;
            case "property": payload[invalid] = false; break;
            case "element-unicode": payload["future"] = JsonValue.Create(JsonSerializer.Deserialize<JsonElement>("\"\\ud800\"")); break;
            case "custom-container": payload["future"] = JsonValue.Create(new Dictionary<string, string> { ["text"] = invalid }); break;
            case "dom-duplicates": payload["future"] = JsonNode.Parse("{\"x\":1,\"x\":2}"); break;
            case "model-alias": payload["MODEL"] = "other"; break;
            case "stream-alias": payload["Stream"] = false; break;
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); if (fault == "cancel") cancel.Cancel();
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new OpenAiHttpFactory(handler); var policy = new OpenAiHttpOptions { MaximumTextCompletionRequestBytes = fault == "budget" ? 1 : null }; var provider = new OpenAiLlmProvider(factory, policy);
        var failure = await Record.ExceptionAsync(() => Complete(provider, cancel.Token, payload)); Assert.NotNull(failure); if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure); if (fault == "budget") Assert.IsType<InvalidDataException>(failure); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>完整根、实际模型和唯一认证必须明确有效，不因遗漏 Key 自动匿名。</summary>
    [Theory] [InlineData("relative")] [InlineData("query")] [InlineData("fragment")] [InlineData("userinfo")] [InlineData("model-unicode")] [InlineData("anonymous")] [InlineData("external")] [InlineData("key")]
    public async Task TargetAndCredentialFault_PrecedesFactory(string fault)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new OpenAiHttpFactory(handler); var provider = new OpenAiLlmProvider(factory); var target = Target(); var credential = new LlmCredential("text-key", Key);
        target = fault switch { "relative" => target with { BaseAddress = new("relative", UriKind.Relative) }, "query" => target with { BaseAddress = new("https://native.invalid/v1/?x=1") }, "fragment" => target with { BaseAddress = new("https://native.invalid/v1/#x") }, "userinfo" => target with { BaseAddress = new("https://a:b@native.invalid/v1/") }, "model-unicode" => target with { UpstreamModel = new string((char)0xd800, 1) }, _ => target };
        credential = fault switch { "anonymous" => LlmCredential.Anonymous("text-key"), "external" => LlmCredential.External("text-key", "principal"), "key" => new("text-key", "bad key"), _ => credential };
        var failure = await Record.ExceptionAsync(() => Complete(provider, TestContext.Current.CancellationToken, context: Context(provider, target, credential))); Assert.NotNull(failure); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>新增策略只允许正值，构造期就失败。</summary>
    [Theory] [InlineData(0, true)] [InlineData(-1, true)] [InlineData(0, false)] [InlineData(-1, false)]
    public void InvalidLocalPolicy_IsRejected(int value, bool request)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new OpenAiHttpFactory(handler);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenAiLlmProvider(factory, request ? new OpenAiHttpOptions { MaximumTextCompletionRequestBytes = value } : new() { MaximumTrackedTextChoices = value })); Assert.Empty(factory.Clients);
    }
    /// <summary>不完整或 Chat 结构结果不能伪装为文本补全；原生稀疏索引始终保持精确。</summary>
    [Theory] [InlineData("missing")] [InlineData("null")] [InlineData("empty")] [InlineData("duplicate")] [InlineData("negative")]
    [InlineData("fraction")] [InlineData("overflow")] [InlineData("text-null")] [InlineData("text-number")] [InlineData("text-missing")]
    [InlineData("finish-null")] [InlineData("finish-empty")] [InlineData("finish-number")] [InlineData("logs-array")] [InlineData("tokens-object")]
    public async Task MalformedCompleteResult_IsNotSuccess(string fault)
    {
        var raw = JsonNode.Parse(Part("result").GetRawText())!.AsObject(); var first = raw["choices"]![0]!.AsObject();
        switch (fault)
        {
            case "missing": raw.Remove("choices"); break; case "null": raw["choices"] = null; break; case "empty": raw["choices"] = new JsonArray(); break;
            case "duplicate": raw["choices"]!.AsArray().Add(first.DeepClone()); break; case "negative": first["index"] = -1; break; case "fraction": first["index"] = 1.5; break; case "overflow": first["index"] = JsonNode.Parse("9223372036854775808"); break;
            case "text-null": first["text"] = null; break; case "text-number": first["text"] = 1; break; case "text-missing": first.Remove("text"); break;
            case "finish-null": first["finish_reason"] = null; break; case "finish-empty": first["finish_reason"] = ""; break; case "finish-number": first["finish_reason"] = 1; break;
            case "logs-array": first["logprobs"] = new JsonArray(); break; case "tokens-object": first["logprobs"]!["tokens"] = new JsonObject(); break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(new OllamaBodyStream(Bytes(raw.ToJsonString())), false))); var factory = new OpenAiHttpFactory(handler); await Assert.ThrowsAsync<InvalidDataException>(() => Complete(new(factory), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls); Assert.True(Assert.Single(factory.Clients).Disposed);
    }
    /// <summary>选中 Bearer 之外的 Key/签名默认头不可发送，借用工厂默认配置不被改写。</summary>
    [Theory] [InlineData("api-key")] [InlineData("x-api-key")] [InlineData("x-goog-api-key")] [InlineData("x-amz-date")] [InlineData("x-amz-security-token")] [InlineData("x-amz-content-sha256")]
    public async Task ForeignAuthDefaults_FailBeforeSend(string name)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("must not send")); var factory = new OpenAiHttpFactory(handler) { Configure = c => c.DefaultRequestHeaders.TryAddWithoutValidation(name, "other") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(new(factory), TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls); var client = Assert.Single(factory.Clients); Assert.True(client.Disposed); Assert.Equal("other", Assert.Single(client.DefaultRequestHeaders.GetValues(name)));
    }
}
