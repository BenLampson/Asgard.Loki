using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>独立原生样本和可观测网络替身，不使用供应商输出生成预期值。</summary>
internal static class OpenAiHttpFixture
{
    internal const string ChatFinish = "{\"id\":\"c\",\"choices\":[{\"index\":2,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}";
    internal const string ResponseFinish = "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}}";
    /// <summary>显式完整目标，无模型名或云环境推断。</summary>
    internal static LlmAdapterContext Context(ILlmProvider provider, string root = "https://upstream.invalid/custom/v1/")
        => new(new LlmTarget { Id = "a", ProviderId = provider.Id, BaseAddress = new(root), UpstreamModel = "actual-a", CredentialId = "key-a" }, new("key-a", "test-key-a"), "public", "session");
    /// <summary>从独立嵌入清单加载原始请求、响应和逐帧快照。</summary>
    internal static JsonElement Golden(string name)
    {
        using var stream = typeof(OpenAiHttpFixture).Assembly.GetManifestResourceStream($"Asgard.Loki.LLM.OpenAI.Tests.Fixtures.{name}.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>原生 SSE 完整事件，未知 event/id/retry 不丢失。</summary>
    internal static string Frame(string json) => "data: " + json + "\n\n";
    /// <summary>终态后仍有真实 EOF；DONE 不是本地合成完成。</summary>
    internal static string Finished(bool responses) => Frame(responses ? ResponseFinish : ChatFinish) + (responses ? "" : Frame("[DONE]"));
    /// <summary>响应内容拥有底层流，可独立配置声明长度。</summary>
    internal static HttpResponseMessage Reply(OllamaBodyStream body, bool streaming, long? length = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StreamContent(body); content.Headers.ContentType = new MediaTypeHeaderValue(streaming ? "text/event-stream" : "application/json");
        content.Headers.ContentLength = length; return new(status) { Content = content };
    }
    /// <summary>完整枚举公开原生事件，并观察成功终态能否逃逸。</summary>
    internal static async Task Drain(OpenAiLlmProvider provider, bool responses, CancellationToken token, List<LlmJsonEvent> output, Action<LlmJsonEvent>? observe = null)
    {
        if (responses) await foreach (var item in ((IResponsesAdapter)provider).StreamAsync(new JsonObject(), Context(provider), token)) { output.Add(item); observe?.Invoke(item); }
        else await foreach (var item in provider.StreamAsync(new JsonObject(), Context(provider), token)) { output.Add(item); observe?.Invoke(item); }
    }
    /// <summary>完整 JSON 入口同样不绕过 EOF 和释放检查。</summary>
    internal static async Task<JsonElement> Complete(OpenAiLlmProvider provider, bool responses, CancellationToken token)
        => responses ? (await provider.CreateAsync(new JsonObject(), Context(provider), token)).Data : (await provider.CompleteAsync(new JsonObject(), Context(provider), token)).Data;
    /// <summary>严格 UTF-8，测试不得由编码替换掩盖无效字节。</summary>
    internal static byte[] Bytes(string body) => new UTF8Encoding(false, true).GetBytes(body);
}
