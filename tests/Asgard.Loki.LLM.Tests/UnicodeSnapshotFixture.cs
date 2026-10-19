using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Xunit;

namespace Asgard.Loki.LLM.Tests;

/// <summary>直接构造原生 DOM，不先序列化坏字符串；替身只观察公共 SDK 已有快照。</summary>
internal static class UnicodeSnapshotFixture
{
    internal static LlmOptions Options(ILlmProvider provider, ILlmSessionStore store) => new()
    {
        Providers = [provider], Credentials = [new("key", "fixture-key")], SessionStore = store,
        Routes = [new() { Model = "public", Targets = [new() { Id = "target", ProviderId = provider.Id, BaseAddress = new("https://wire.invalid/v1/"), UpstreamModel = "actual", CredentialId = "key" }] }]
    };
    internal static ILokiClient Build(ILlmProvider provider, ILlmSessionStore store) => new LokiClientBuilder().AddLLM(Options(provider, store)).Build();
    /// <summary>保持可识别的工具历史原生结构；只在单个明确字段中制造非法 UTF-16。</summary>
    internal static JsonArray Body(bool responses)
        => JsonNode.Parse(responses ? """
        [{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]},
         {"type":"function_call","call_id":"call","name":"inspect","arguments":"{}"},
         {"type":"function_call_output","call_id":"call","output":"ok"}]
        """ : """
        [{"role":"user","content":[{"type":"text","text":"hello"}]},
         {"role":"assistant","tool_calls":[{"id":"call","type":"function","function":{"name":"inspect","arguments":"{}"}}]},
         {"role":"tool","tool_call_id":"call","content":"ok"}]
        """)!.AsArray();
    /// <summary>实际通过两个公共协议客户端，避免辅助反序列化提前替换数据。</summary>
    internal static async Task Invoke(ILokiClient client, bool responses, bool stream, JsonArray body, Dictionary<string, JsonElement>? parameters = null, string model = "public", string session = "unicode", string? previous = null)
    {
        var token = TestContext.Current.CancellationToken; var call = new LlmCallOptions { SessionId = session };
        if (responses)
        {
            var request = new ResponseRequest { Model = model, Input = body, Parameters = parameters ?? [], PreviousResponseId = previous };
            if (stream) { await foreach (var _ in client.LLM.Responses.StreamAsync(request, call, token)) { } } else await client.LLM.Responses.CreateAsync(request, call, token);
        }
        else
        {
            var request = new ChatCompletionRequest { Model = model, Messages = body, Parameters = parameters ?? [] };
            if (stream) { await foreach (var _ in client.LLM.ChatCompletions.StreamAsync(request, call, token)) { } } else await client.LLM.ChatCompletions.CompleteAsync(request, call, token);
        }
    }
}
