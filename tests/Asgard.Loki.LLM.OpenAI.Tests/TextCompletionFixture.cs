using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>独立原生文本窗口和真实公共 SDK；只在 Session 行为中使用内存缓存与明确测试协调器。</summary>
internal static class TextCompletionFixture
{
    internal const string Key = "fixture-text-key";
    /// <summary>严格 UTF-8 输入字节和完整原生数据帧。</summary>
    internal static byte[] Bytes(string value) => OpenAiHttpFixture.Bytes(value);
    /// <summary>测试帧的分隔遵循 SSE 原文，不在测试中生成供应商结果预期。</summary>
    internal static string Frame(string value) => OpenAiHttpFixture.Frame(value);
    /// <summary>独立完整请求/结果清单。</summary>
    internal static JsonElement Part(string name) => OpenAiHttpFixture.Golden("Text.calls").GetProperty(name).Clone();
    /// <summary>读取分别手工定义的事件 JSON 与实际 SSE 字节窗口。</summary>
    internal static string Text(string name)
    { using var source = typeof(TextCompletionFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.OpenAI.Tests.Fixtures.Text." + name)!; using var reader = new StreamReader(source); return reader.ReadToEnd(); }
    /// <summary>独立原生请求 DOM。</summary>
    internal static JsonObject Payload() => JsonNode.Parse(Part("caller").GetRawText())!.AsObject();
    /// <summary>实际公共 SDK 文本请求。</summary>
    internal static TextCompletionRequest Request() => Part("caller").Deserialize<TextCompletionRequest>()!;
    /// <summary>完整目标显式声明文本协议，不推断模型或地址。</summary>
    internal static LlmTarget Target() => new() { Id = "text-target", ProviderId = "openai", BaseAddress = new(Part("root").GetString()!), UpstreamModel = Part("model").GetString()!, CredentialId = "text-key", Protocols = LlmProtocol.TextCompletions };
    /// <summary>直接适配器与 SDK 使用同一目标和独立凭据。</summary>
    internal static LlmAdapterContext Context(OpenAiLlmProvider provider, LlmTarget? target = null, LlmCredential? key = null) => new(target ?? Target() with { ProviderId = provider.Id }, key ?? new("text-key", Key), "public-text", "text-session");
    /// <summary>内存分布式缓存配合明确协调器，只验证真实 Session 行为。</summary>
    internal static ILokiClient Build(OpenAiHttpFactory factory, OpenAiHttpOptions? policy = null, ILlmSessionStore? store = null, LlmTarget? target = null, string key = Key, string client = "Asgard.Loki.LLM.OpenAI") => new LokiClientBuilder().AddLLM(new LlmOptions
    { Providers = [new OpenAiLlmProvider(factory, policy ?? new(), httpClientName: client)], Credentials = [new("text-key", key)], SessionStore = store ?? CompactionFixture.Store(), Routes = [new() { Model = "public-text", Targets = [target ?? Target()] }] }).Build();
    /// <summary>响应拥有正文，可分别注入类型、长度和 HTTP 状态。</summary>
    internal static HttpResponseMessage Reply(Stream source, bool stream, long? length = null, int status = 200)
    { var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(source) }; result.Content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); result.Content.Headers.ContentLength = length; return result; }
    /// <summary>直接完整调用同一真实供应商接口。</summary>
    internal static async Task<JsonElement> Complete(OpenAiLlmProvider provider, CancellationToken token, JsonObject? payload = null, LlmAdapterContext? context = null) => (await ((ITextCompletionsAdapter)provider).CompleteAsync(payload ?? Payload(), context ?? Context(provider), token)).Data;
    /// <summary>完整消费以观察迟到故障是否已经泄漏结束帧。</summary>
    internal static async Task<List<TextCompletionEvent>> Stream(OpenAiLlmProvider provider, CancellationToken token, JsonObject? payload = null, Action<TextCompletionEvent>? observe = null)
    { List<TextCompletionEvent> items = []; await foreach (var item in ((ITextCompletionsAdapter)provider).StreamAsync(payload ?? Payload(), Context(provider), token)) { observe?.Invoke(item); items.Add(item); } return items; }
    /// <summary>故障用例从独立原生帧组装网络碎片，不从实现输出创建预期。</summary>
    internal static string Wire(IEnumerable<JsonElement> events, bool done = true) => string.Concat(events.Select(e => Frame(JsonSerializer.Serialize(e)))) + (done ? Frame("[DONE]") : "");
    /// <summary>独立预期事件列表。</summary>
    internal static JsonElement[] Events() { using var doc = JsonDocument.Parse(Text("events.json")); return doc.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray(); }
    /// <summary>原生选择项结束不等于整个流已关闭。</summary>
    internal static bool Ending(TextCompletionEvent item) => item.Data.TryGetProperty("choices", out var choices) && choices.EnumerateArray().Any(c => c.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null);
}
