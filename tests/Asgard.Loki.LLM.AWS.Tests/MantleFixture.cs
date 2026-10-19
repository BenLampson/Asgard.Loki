using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Tests;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>完整独立 Mantle 报文和公开 SDK 装配，不运行签名器来生成预期。</summary>
internal static class MantleFixture
{
    internal const string Start = "{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"content\":[]}}";
    internal const string Finish = "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"}}";
    internal const string Stop = "{\"type\":\"message_stop\"}";
    /// <summary>严格字节，不让错误测试先被替换字符修复。</summary>
    internal static byte[] Bytes(string value) => new UTF8Encoding(false, true).GetBytes(value);
    /// <summary>完整 JSON 原始资源。</summary>
    internal static JsonElement Golden(string name) => JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("mantle-" + name));
    /// <summary>请求包含全部原生窗口，不从 SDK 输出回填。</summary>
    internal static JsonObject Request() => JsonNode.Parse(AwsFixture.Text("mantle-request"))!.AsObject();
    /// <summary>明确原生 SSE 事件。</summary>
    internal static string Frame(string json) => "data: " + json + "\n\n";
    /// <summary>最小消息仍具备开始、结束原因及 stop。</summary>
    internal static string Finished => Frame(Start) + Frame(Finish) + Frame(Stop);
    /// <summary>本次内容拥有原始流，声明长度可独立测试。</summary>
    internal static HttpResponseMessage Reply(Stream source, bool stream, long? length = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var content = new StreamContent(source); content.Headers.ContentType = new(stream ? "text/event-stream" : "application/json"); content.Headers.ContentLength = length; return new(status) { Content = content };
    }
    /// <summary>仅配置 native 消息根，不推断区域、模型或路径。</summary>
    internal static LlmTarget Target(string providerId = "aws-mantle", string id = "first", string root = "https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/", string model = "anthropic.configured-model")
        => new() { Id = id, ProviderId = providerId, BaseAddress = new(root), UpstreamModel = model, CredentialId = "identity", Protocols = LlmProtocol.ClaudeMessages };
    /// <summary>同一主体及完整目标用于直接调用。</summary>
    internal static LlmAdapterContext Context(LlmTarget? target = null, LlmCredential? credential = null)
        => new(target ?? Target(), credential ?? LlmCredential.External("identity", "principal-a"), "public", "mantle-session");
    /// <summary>公共客户端借用真实内存存储及显式测试协调器。</summary>
    internal static ILokiClient Client(ILlmProvider provider, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0, string principal = "principal-a")
        => new LokiClientBuilder().AddLLM(new LlmOptions { Providers = [provider], Credentials = [LlmCredential.External("identity", principal)], SessionStore = store ?? AwsFixture.Store(), TargetSelector = new TestTargetSelector(selected), Routes = [new LlmRoute { Model = "public", Targets = targets ?? [Target(provider.Id)] }] }).Build();
    /// <summary>完整消费以观察晚到异常，允许检查暂存终态的交付时刻。</summary>
    internal static async Task Drain(AwsMantleLlmProvider provider, CancellationToken token, List<ClaudeMessageEvent> seen, Action<ClaudeMessageEvent>? observe = null)
    {
        await foreach (var item in provider.StreamAsync(Request(), Context(), token)) { seen.Add(item); observe?.Invoke(item); }
    }
}
