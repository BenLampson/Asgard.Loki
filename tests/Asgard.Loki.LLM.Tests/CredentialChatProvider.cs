using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tests;

/// <summary>显式声明认证支持的离线 Chat 供应商，不读取虚构 Key，也不发起网络请求。</summary>
internal sealed class CredentialChatProvider(bool anonymous = true, bool apiKey = true, string version = "1", bool external = false) : IChatCompletionsAdapter, ILlmCredentialSupport
{
    /// <inheritdoc />
    public string Id => "credential-chat";
    /// <inheritdoc />
    public string BindingIdentity => "credential-chat-v" + version;
    /// <inheritdoc />
    public bool SupportsCredential(LlmCredentialKind kind) => kind == LlmCredentialKind.Anonymous && anonymous || kind == LlmCredentialKind.ApiKey && apiKey || kind == LlmCredentialKind.External && external;
    /// <summary>保留本次请求和完整上下文以核对实际绑定。</summary>
    internal ConcurrentQueue<(JsonObject Request, LlmAdapterContext Context)> Calls { get; } = new();
    /// <inheritdoc />
    public Task<ChatCompletion> CompleteAsync(JsonObject request, LlmAdapterContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue((request.DeepClone().AsObject(), context));
        return Task.FromResult(new ChatCompletion(JsonSerializer.SerializeToElement(new
        {
            id = "credential-reply", model = context.Target.UpstreamModel, created = 0,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "done" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 1, completion_tokens = 2, total_tokens = 3 }
        }), context.Metadata));
    }
    /// <inheritdoc />
    public async IAsyncEnumerable<ChatCompletionEvent> StreamAsync(JsonObject request, LlmAdapterContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask; cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue((request.DeepClone().AsObject(), context));
        yield return Event(new { role = "assistant", content = "done" }, null, context);
        yield return Event(new { }, "stop", context, new { prompt_tokens = 1, completion_tokens = 2, total_tokens = 3 });
    }
    /// <summary>独立原生分块；认证方式不会被塞入协议正文。</summary>
    private static ChatCompletionEvent Event(object delta, string? finish, LlmAdapterContext context, object? usage = null)
        => new(JsonSerializer.SerializeToElement(new { id = "credential-reply", model = context.Target.UpstreamModel, created = 0,
            choices = new[] { new { index = 0, delta, finish_reason = finish } }, usage }), context.Metadata);
}
