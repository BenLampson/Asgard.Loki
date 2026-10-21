using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>API Key 模式只替换认证，原生完整 JSON/二进制样本仍由既有独立金样提供。</summary>
internal static class ApiKeyFixture
{
    internal const string Key = "fixture-bedrock-api-key";
    /// <summary>四个已实现的原生调用方法，兼容原生直接入口和公共 SDK。</summary>
    public static TheoryData<bool, bool> Operations => new() { { false, false }, { false, true }, { true, false }, { true, true } };
    /// <summary>明确选择原生协议，不通过模型名猜测；ARN 和代理根路径保留。</summary>
    internal static LlmTarget Target(bool converse, string id = "first") => converse ? ConverseFixture.Target(id) : AwsFixture.Target(id);
    internal static JsonObject Request(bool converse) => converse ? ConverseFixture.Request() : AwsFixture.Request();
    internal static byte[] Bytes(bool converse, bool stream) => stream ? converse ? ConverseStreamFixture.Bytes() : StreamFixture.Bytes() : Encoding.UTF8.GetBytes(AwsFixture.Text(converse ? "converse-response" : "response"));
    internal static HttpResponseMessage Reply(bool stream, Stream body) => stream ? StreamFixture.Reply(body) : new(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("application/json") } } };
    internal static LlmAdapterContext Context(bool converse, LlmTarget? target = null, LlmCredential? credential = null) => new(target ?? Target(converse), credential ?? new("identity", Key), "public", "key-session");
    internal static ILokiClient Client(AwsLlmProvider provider, bool converse, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, LlmCredential? credential = null) => AwsFixture.Client(provider, store, targets ?? [Target(converse)], selected, credential ?? new("identity", Key));
    /// <summary>直接接口使用不可变所选凭据和目标，不读取任何外部 Key。</summary>
    internal static async Task<LlmJsonResult> Complete(AwsLlmProvider provider, bool converse, JsonObject? input = null, LlmAdapterContext? context = null, CancellationToken token = default)
        => converse ? await provider.ConverseAsync(input ?? Request(true), context ?? Context(true), token) : await provider.CreateAsync(input ?? Request(false), context ?? Context(false), token);
    internal static async IAsyncEnumerable<LlmJsonEvent> Stream(AwsLlmProvider provider, bool converse, JsonObject? input = null, LlmAdapterContext? context = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        if (converse) { await foreach (var item in provider.ConverseStreamAsync(input ?? Request(true), context ?? Context(true), token)) yield return item; }
        else { await foreach (var item in provider.StreamAsync(input ?? Request(false), context ?? Context(false), token)) yield return item; }
    }
    /// <summary>终态保留已观察事件，以验证晚期异常之前没有成功泄漏。</summary>
    internal static async Task<List<LlmJsonResult>> Invoke(AwsLlmProvider provider, bool converse, bool stream, CancellationToken token, Action<LlmJsonEvent>? observe = null, JsonObject? input = null, LlmAdapterContext? context = null)
    {
        List<LlmJsonResult> result = []; if (!stream) result.Add(await Complete(provider, converse, input, context, token)); else await foreach (var item in Stream(provider, converse, input, context, token)) { result.Add(item); observe?.Invoke(item); } return result;
    }
    internal static async Task<LlmJsonResult> PublicComplete(ILokiClient client, bool converse, CancellationToken token) => converse
        ? await client.LLM.BedrockConverse.ConverseAsync(Request(true).Deserialize<BedrockConverseRequest>()!, new() { SessionId = "key-session" }, token)
        : await client.LLM.ClaudeMessages.CreateAsync(Request(false).Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "key-session" }, token);
    internal static async IAsyncEnumerable<LlmJsonEvent> PublicStream(ILokiClient client, bool converse, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (converse) { await foreach (var item in client.LLM.BedrockConverse.StreamAsync(Request(true).Deserialize<BedrockConverseRequest>()!, new() { SessionId = "key-session" }, token)) yield return item; }
        else { await foreach (var item in client.LLM.ClaudeMessages.StreamAsync(Request(false).Deserialize<ClaudeMessageRequest>()!, new() { SessionId = "key-session" }, token)) yield return item; }
    }
    /// <summary>Converse 只按传输名识别，Claude 按原生 type 识别，不以普通文本猜测终态。</summary>
    internal static bool Terminal(LlmJsonEvent item) => item is BedrockConverseEvent ? item.EventName is "messageStop" or "metadata" : item.Type is "message_stop" || item.Data.TryGetProperty("delta", out var delta) && delta.TryGetProperty("stop_reason", out _);
}
