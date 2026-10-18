using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Asgard.Loki.LLM;

namespace Asgard.Loki.Internal;

/// <summary>由原生供应商独立编译的无重试 HTTP/SSE 收发辅助。</summary>
internal static class NativeLlmHttp
{
    /// <summary>读取独立 JSON 快照并保留 HTTP 错误正文。</summary>
    internal static async Task<JsonElement> SendAsync(IHttpClientFactory factory, string name,
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        using (var client = factory.CreateClient(name))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.Clone();
        }
    }

    /// <summary>保留未知事件；终态判定由各供应商负责。</summary>
    internal static async IAsyncEnumerable<SseFrame> StreamAsync(IHttpClientFactory factory, string name,
        HttpRequestMessage request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (request)
        using (var client = factory.CreateClient(name))
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                throw new LlmUpstreamException("流式调用未返回 SSE。", response.StatusCode,
                    await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var frame in SseReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false)) yield return frame;
        }
    }

    /// <summary>错误不会导致重试或目标切换。</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw new LlmUpstreamException("原生 LLM 调用失败。", response.StatusCode,
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }
}
