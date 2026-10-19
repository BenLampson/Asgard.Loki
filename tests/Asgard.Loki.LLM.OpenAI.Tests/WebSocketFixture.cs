using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>通过真实 SDK 与供应商打开内存原生连接，禁止任何 HTTP 网络回退。</summary>
internal static class WebSocketFixture
{
    /// <summary>提供已声明原生传输的完整目标。</summary>
    internal static LlmTarget Target(string id = "a") => CompactionFixture.Target(id, LlmTargetFeatures.ResponsesWebSocket);
    /// <summary>使用借用的替代工厂和标准内存会话依赖。</summary>
    internal static ILokiClient Build(StubResponsesWebSocketFactory factory, OpenAiResponsesWebSocketOptions? options = null,
        ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, ILlmTargetSelector? selector = null)
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("WebSocket 不能隐式回退 HTTP。"));
        var provider = new OpenAiLlmProvider(new StubHttpClientFactory(handler), options ?? new(), factory);
        return CompactionFixture.Build(handler, store, targets ?? [Target()], selector, provider);
    }
    /// <summary>公开调用的逻辑会话。</summary>
    internal static LlmCallOptions Call(string id = "session") => CompactionFixture.Call(id);
    /// <summary>SDK 负责把 public 映射为实际模型。</summary>
    internal static JsonObject Create(string? lane = null, string? eventId = null)
    {
        var value = new JsonObject { ["type"] = "response.create", ["input"] = "hello" };
        if (lane is not null) value["stream_id"] = lane; if (eventId is not null) value["event_id"] = eventId; return value;
    }
    /// <summary>独立构造生命周期，不复制供应商转换代码的预期输出。</summary>
    internal static string Lifecycle(string type, string id = "resp_1", string? lane = null, int? sequence = null, string? previous = null)
    {
        var value = new JsonObject { ["type"] = type, ["response"] = new JsonObject { ["id"] = id, ["status"] = type switch
            { "response.completed" => "completed", "response.failed" => "failed", "response.incomplete" => "incomplete", "response.queued" => "queued", _ => "in_progress" }, ["output"] = new JsonArray() } };
        if (lane is not null) value["stream_id"] = lane; if (sequence is not null) value["sequence_number"] = sequence;
        if (previous is not null) value["response"]!["previous_response_id"] = previous; return value.ToJsonString();
    }
    /// <summary>只读事件数据，所有生成调用均由测试显式发送。</summary>
    internal static async Task<List<JsonElement>> Drain(IResponsesWebSocketConnection connection, CancellationToken token)
    {
        List<JsonElement> result = []; await foreach (var item in connection.ReadEventsAsync(token)) result.Add(item.Data); return result;
    }
}
