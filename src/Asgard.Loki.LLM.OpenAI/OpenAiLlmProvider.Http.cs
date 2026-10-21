using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
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
        if (_httpOptions.Transport is null) return OpenAiHttpTransport.PostAsync(Request(path, payload, context), _httpClientFactory, _httpClientName, _httpOptions, token);
        OpenAiHttpJson.Input(payload); var call = new OpenAiConfiguredHttpCall(Encoding.UTF8.GetBytes(payload.ToJsonString()), context, _httpOptions.Transport, path, false);
        return OpenAiHttpTransport.PostAsync(call.Request, _httpClientFactory, _httpClientName, _httpOptions, token, call.BeforeSend, call.Verify, rawErrors: true);
    }
    /// <summary>共享原生帧处理，不复制模型、工具或终态业务规则。</summary>
    private async IAsyncEnumerable<SseFrame> StreamProtocolAsync(string path, JsonObject payload, LlmAdapterContext context, bool responses, [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OpenAiConfiguredHttpCall? call = null;
        if (_httpOptions.Transport is not null) { OpenAiHttpJson.Input(payload); call = new(Encoding.UTF8.GetBytes(payload.ToJsonString()), context, _httpOptions.Transport, path, true); }
        await foreach (var frame in OpenAiHttpTransport.StreamAsync(call?.Request ?? Request(path, payload, context), _httpClientFactory, _httpClientName, _httpOptions, responses, token,
            call is null ? null : call.BeforeSend, call is null ? null : call.Verify, rawErrors: call is not null).ConfigureAwait(false)) yield return frame;
    }
}
