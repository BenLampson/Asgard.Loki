using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.OpenAI.Tests.OpenAiHttpFixture;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>完整独立样本覆盖公共 SDK 的路由、原生字段、工具关联和逐帧传输快照。</summary>
public sealed class HttpSnapshotTests
{
    /// <summary>JSON/SSE 通过实际入口调用；只改模型和方法流标志，原生内容逐字段保留。</summary>
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task PublicSdk_PreservesFullNativeGolden(bool responses, bool streaming)
    {
        var golden = Golden(responses ? "responses-http" : "chat-http"); var expected = JsonNode.Parse(golden.GetProperty("request").GetRawText())!.AsObject();
        expected["stream"] = streaming;
        var wire = streaming ? golden.GetProperty("wire").GetString()! : golden.GetProperty("complete").GetRawText();
        var body = new OllamaBodyStream(Bytes(wire));
        using var handler = new StubHttpHandler(async (request, token) =>
        {
            Assert.Equal("https://a.invalid/custom/v1/" + (responses ? "responses" : "chat/completions"), request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("test-key-a", request.Headers.Authorization.Parameter);
            var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token)); Assert.True(JsonNode.DeepEquals(expected, actual));
            Assert.Equal("9007199254740993", actual!["future"]!["integer"]!.ToJsonString());
            return Reply(body, streaming, Bytes(wire).Length);
        });
        var factory = new OpenAiHttpFactory(handler); var target = CompactionFixture.Target() with { Protocols = LlmProtocol.ChatCompletions | LlmProtocol.Responses };
        await using var sdk = CompactionFixture.Build(handler, targets: [target], provider: new OpenAiLlmProvider(factory));
        var requestNode = JsonNode.Parse(golden.GetProperty("request").GetRawText())!.AsObject(); requestNode["model"] = "public";
        List<LlmJsonEvent> items = []; JsonElement result = default;
        if (responses)
        {
            var request = requestNode.Deserialize<ResponseRequest>()!;
            if (streaming) await foreach (var item in sdk.LLM.Responses.StreamAsync(request, CompactionFixture.Call(), TestContext.Current.CancellationToken)) items.Add(item);
            else result = (await sdk.LLM.Responses.CreateAsync(request, CompactionFixture.Call(), TestContext.Current.CancellationToken)).Data;
        }
        else
        {
            var request = requestNode.Deserialize<ChatCompletionRequest>()!;
            if (streaming) await foreach (var item in sdk.LLM.ChatCompletions.StreamAsync(request, CompactionFixture.Call(), TestContext.Current.CancellationToken)) items.Add(item);
            else result = (await sdk.LLM.ChatCompletions.CompleteAsync(request, CompactionFixture.Call(), TestContext.Current.CancellationToken)).Data;
        }
        if (streaming)
        {
            var frames = golden.GetProperty("events"); Assert.Equal(frames.GetArrayLength(), items.Count);
            for (var i = 0; i < items.Count; i++)
            {
                Assert.True(JsonElement.DeepEquals(frames[i].GetProperty("data"), items[i].Data));
                Assert.Equal(frames[i].GetProperty("event").GetString(), items[i].EventName); Assert.Equal(frames[i].GetProperty("id").GetString(), items[i].EventId);
                Assert.Equal(frames[i].GetProperty("retry").GetInt64(), items[i].RetryMilliseconds); Assert.Equal("public", items[i].Metadata.PublicModel);
            }
            // 快照额外检查可被错误双精度转换掩盖的原始数字文本。
            Assert.Equal("9007199254740993", items[^1].Data.GetProperty("future").GetProperty("integer").GetRawText());
        }
        else
        {
            Assert.True(JsonElement.DeepEquals(golden.GetProperty("complete"), result));
            Assert.Equal("9007199254740993", result.GetProperty("future").GetProperty("integer").GetRawText());
        }
        Assert.True(body.Eof); Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }

    /// <summary>完整 JSON 读取、释放或最终取消失败不能返回成功结果。</summary>
    [Theory]
    [InlineData(false, "read")] [InlineData(true, "read")]
    [InlineData(false, "body")] [InlineData(true, "body")]
    [InlineData(false, "client")] [InlineData(true, "client")]
    [InlineData(false, "eof-cancel")] [InlineData(true, "eof-cancel")]
    [InlineData(false, "body-cancel")] [InlineData(true, "body-cancel")]
    [InlineData(false, "client-cancel")] [InlineData(true, "client-cancel")]
    public async Task CompleteJson_PropagatesLateFaults(bool responses, string fault)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new OllamaBodyStream(Bytes("{\"id\":\"native\",\"future\":false}"))
        { FailAtEof = fault == "read", FailDispose = fault == "body", OnEof = fault == "eof-cancel" ? cancel.Cancel : null, OnDispose = fault == "body-cancel" ? cancel.Cancel : null };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Reply(body, false)));
        var factory = new OpenAiHttpFactory(handler) { OnDispose = () => { if (fault == "client") throw new IOException("client dispose"); if (fault == "client-cancel") cancel.Cancel(); } };
        var error = await Record.ExceptionAsync(() => Complete(new(factory), responses, cancel.Token));
        if (fault.EndsWith("cancel", StringComparison.Ordinal)) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<IOException>(error);
        Assert.True(body.Disposed); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls);
    }
}
