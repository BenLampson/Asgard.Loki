using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Infrastructure;
using Asgard.Loki.LLM.OpenAI.Internal;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>构造已绑定原生请求，复用供应商内部 HTTP/JSON 与 SSE 传输及独立原生状态。</summary>
public sealed partial class OpenAiLlmProvider
{
    /// <summary>完整结果的字段、资源和 EOF 契约保持不变。</summary>
    private Task<JsonElement> PostAsync(string path, JsonObject payload, LlmAdapterContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return OpenAiHttpTransport.PostAsync(Request(path, payload, context), _httpClientFactory, _httpClientName, _httpOptions, token);
    }
    /// <summary>共享原生帧处理，不复制模型、工具或终态业务规则。</summary>
    private async IAsyncEnumerable<SseFrame> StreamProtocolAsync(string path, JsonObject payload, LlmAdapterContext context, bool responses, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await foreach (var frame in OpenAiHttpTransport.StreamAsync(Request(path, payload, context), _httpClientFactory, _httpClientName, _httpOptions, responses, token).ConfigureAwait(false)) yield return frame;
    }
}
