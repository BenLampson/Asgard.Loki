using System.Text.Json;
using Asgard.Loki.Abstractions;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>完整公共 SDK、内存会话与替代 WebSocket 的独立 Realtime 调用窗口。</summary>
internal static class RealtimeFixture
{
    /// <summary>Realtime 是独立协议，不从 Responses 可选操作推断。</summary>
    internal static LlmTarget Target(string id = "a")
        => CompactionFixture.Target(id, LlmTargetFeatures.None) with { Protocols = LlmProtocol.Realtime };
    /// <summary>构造期间及所有调用禁止回退真实 HTTP。</summary>
    internal static ILokiClient Build(StubResponsesWebSocketFactory factory, OpenAiRealtimeOptions? options = null,
        ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, ILlmTargetSelector? selector = null)
    {
        var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("Realtime 不能回退 HTTP。"));
        return CompactionFixture.Build(handler, store, targets ?? [Target()], selector,
            new OpenAiLlmProvider(new StubHttpClientFactory(handler), options ?? new(), factory));
    }
    /// <summary>读取完整独立原生样本。</summary>
    internal static JsonElement Transcript()
    {
        using var source = typeof(RealtimeFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.OpenAI.Tests.Fixtures.realtime.json")!;
        using var document = JsonDocument.Parse(source); return document.RootElement.Clone();
    }
    /// <summary>只收集原生消息，不根据错误或响应终态提前完成连接。</summary>
    internal static async Task<List<RealtimeEvent>> Drain(IRealtimeConnection connection, CancellationToken cancellationToken)
    {
        List<RealtimeEvent> result = [];
        await foreach (var item in connection.ReadEventsAsync(cancellationToken)) result.Add(item);
        return result;
    }
}
