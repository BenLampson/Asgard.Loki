using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>完整独立 Nova JSON 原生样本和公开 SDK 装配，不运行供应商生成预期。</summary>
internal static class NovaFixture
{
    /// <summary>v1/v2 有各自独立的正文和输出；不混用版本缺省。</summary>
    internal static JsonElement Golden(string version = "v1") => JsonSerializer.Deserialize<JsonElement>(AwsFixture.Text("nova-native")).GetProperty(version).Clone();
    /// <summary>获取新原生请求对象。</summary>
    internal static JsonObject Request(string version = "v1") => JsonNode.Parse(Golden(version).GetProperty("request").GetRawText())!.AsObject();
    /// <summary>使用 SDK 公开的 Native JSON 契约。</summary>
    internal static BedrockNovaRequest Typed(string version = "v1") => Golden(version).GetProperty("request").Deserialize<BedrockNovaRequest>()!;
    /// <summary>Nova 使用明确 Runtime 目标与独立协议，不根据模型自动路由。</summary>
    internal static LlmTarget Target(string model = "us.amazon.nova-lite-v1:0") => AwsFixture.Target() with { UpstreamModel = model, Protocols = LlmProtocol.BedrockNova };
    /// <summary>固定同一外部主体或显式 API Key。</summary>
    internal static LlmAdapterContext Context(LlmTarget? target = null, bool apiKey = false) => AwsFixture.Context(target ?? Target(), apiKey ? new LlmCredential("identity", "fixture-nova-key") : null);
    /// <summary>目标声明才启用原生 Nova，默认不改变其他协议的选择。</summary>
    internal static ILokiClient Client(AwsLlmProvider provider, bool apiKey = false, ILlmSessionStore? store = null, IReadOnlyList<LlmTarget>? targets = null, int selected = 0)
        => AwsFixture.Client(provider, store, targets ?? [Target()], selected, apiKey ? new LlmCredential("identity", "fixture-nova-key") : null);
}
