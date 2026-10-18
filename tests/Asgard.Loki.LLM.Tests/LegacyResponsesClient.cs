namespace Asgard.Loki.LLM.Tests;

/// <summary>模拟只实现旧版基础 Responses 公共接口的调用者自定义客户端。</summary>
internal sealed class LegacyResponsesClient : IResponsesClient
{
    /// <inheritdoc />
    public Task<Response> CreateAsync(ResponseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <inheritdoc />
    public IAsyncEnumerable<ResponseEvent> StreamAsync(ResponseRequest request, LlmCallOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
