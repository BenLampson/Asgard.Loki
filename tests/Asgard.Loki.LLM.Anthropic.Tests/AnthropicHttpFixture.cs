using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.LLM.Tests;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Asgard.Loki.LLM.Anthropic.Tests;

/// <summary>独立 Messages 样本与真实 SDK 的离线构造，不捕获供应商输出来生成预期。</summary>
internal static class AnthropicHttpFixture
{
    internal const string Start = "{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"content\":[]}}";
    internal const string Finish = "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}";
    internal const string Stop = "{\"type\":\"message_stop\"}";
    /// <summary>严格 UTF-8 字节，碎片流逐字节读取。</summary>
    internal static byte[] Bytes(string text) => new UTF8Encoding(false, true).GetBytes(text);
    /// <summary>完整原生 SSE 帧。</summary>
    internal static string Frame(string json) => "data: " + json + "\n\n";
    /// <summary>最小但真实闭合的原生生命周期，不从 EOF 合成消息。</summary>
    internal static string Finished => Frame(Start) + Frame(Finish) + Frame(Stop);
    /// <summary>源响应拥有正文流；可模拟长度和 HTTP 错误。</summary>
    internal static HttpResponseMessage Reply(Stream body, bool stream, long? length = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StreamContent(body); content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); content.Headers.ContentLength = length;
        return new(status) { Content = content };
    }
    /// <summary>嵌入样本包含请求、结果、原始 SSE 和独立逐帧预期。</summary>
    internal static JsonElement Golden(string name = "anthropic")
    {
        using var source = typeof(AnthropicHttpFixture).Assembly.GetManifestResourceStream($"Asgard.Loki.LLM.Anthropic.Tests.Fixtures.{name}.json")!;
        using var json = JsonDocument.Parse(source); return json.RootElement.Clone();
    }
    /// <summary>明确完整目标，不从区域或模型推断端点。</summary>
    internal static LlmTarget Target(ILlmProvider provider, string root = "https://native.invalid/custom/v1/", string model = "actual-claude")
        => new() { Id = "target", ProviderId = provider.Id, BaseAddress = new(root), UpstreamModel = model, CredentialId = "key", Protocols = LlmProtocol.ClaudeMessages };
    /// <summary>直接适配器与公共 SDK 使用同一完整上游身份。</summary>
    internal static LlmAdapterContext Context(ILlmProvider provider) => new(Target(provider), new("key", "test-key"), "public", "session");
    /// <summary>缓存测试只依赖内存和显式测试协调器。</summary>
    internal static DistributedCacheLlmSessionStore Store() => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), new TestSessionLock());
    /// <summary>由调用者明确提供供应商、凭据和完整目标。</summary>
    internal static ILokiClient Build(ILlmProvider provider, ILlmSessionStore? store = null, LlmTarget? target = null, string key = "test-key")
        => new LokiClientBuilder().AddLLM(new LlmOptions { SessionStore = store ?? Store(), Providers = [provider], Credentials = [new("key", key)], Routes = [new LlmRoute { Model = "public", Targets = [target ?? Target(provider)] }] }).Build();
    /// <summary>完整消费以观察迟到的错误和终态是否逃逸。</summary>
    internal static async Task Drain(AnthropicLlmProvider provider, CancellationToken token, List<ClaudeMessageEvent> seen, Action<ClaudeMessageEvent>? observe = null)
    {
        await foreach (var item in provider.StreamAsync(new JsonObject(), Context(provider), token)) { seen.Add(item); observe?.Invoke(item); }
    }
}
