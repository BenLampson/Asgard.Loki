using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;
using Asgard.Loki.LLM.Conversion;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>两种原生 API 的独立完整样本与公共 SDK 装配，不借用生产转换生成目标。</summary>
internal static class BedrockConversionFixture
{
    /// <summary>共享的是测试金样数据，生产供应商与转换包仍无依赖关系。</summary>
    internal static JsonObject Read(string version, string kind)
    {
        using var stream = typeof(BedrockConversionFixture).Assembly.GetManifestResourceStream("BedrockConversion.Fixture")!;
        return JsonNode.Parse(stream)![version]![kind]!.AsObject();
    }
    /// <summary>原生格式与模型参数位置分别明确选择。</summary>
    internal static LlmConversionOptions Options(string version = "v2", bool nested = false) => new()
    {
        BedrockNovaRequestFormat = version == "v1" ? BedrockNovaRequestFormat.MessagesV1 : BedrockNovaRequestFormat.Nova2,
        BedrockConverseInferenceFormat = nested ? BedrockConverseInferenceFormat.NestedInferenceConfig : BedrockConverseInferenceFormat.Direct
    };
    /// <summary>提供一个完整目标，不从模型字符串推断原生协议或认证方式。</summary>
    internal static ILokiClient Client(ILlmProvider provider, LlmProtocol publicProtocol, bool apiKey = false, ILlmSessionStore? store = null)
        => new LokiClientBuilder().AddLLM(new()
        {
            Providers = [provider], Credentials = [apiKey ? new("identity", "fixture-native-key") : LlmCredential.External("identity", "principal-a")],
            SessionStore = store ?? AwsFixture.Store(), Routes = [new() { Model = "public", Targets = [AwsFixture.Target() with
            { ProviderId = provider.Id, Protocols = publicProtocol, UpstreamModel = "caller-model" }] }]
        }).Build();
    /// <summary>仅通过 SDK 的公开原生入口执行，模型改写由原有核心目标绑定完成。</summary>
    internal static async Task<LlmJsonResult> Complete(ILokiClient client, bool nova, JsonObject source, CancellationToken token)
        => nova ? await client.LLM.BedrockNova.InvokeAsync(source.Deserialize<BedrockNovaRequest>()!, new() { SessionId = "bedrock-conversion" }, token)
            : await client.LLM.BedrockConverse.ConverseAsync(source.Deserialize<BedrockConverseRequest>()!, new() { SessionId = "bedrock-conversion" }, token);
    /// <summary>跨协议和同协议流使用相同公共入口，完整 JSON 支持不能隐式授权流式转换。</summary>
    internal static async Task<List<LlmJsonEvent>> Stream(ILokiClient client, bool nova, JsonObject source, CancellationToken token)
    {
        List<LlmJsonEvent> seen = [];
        if (nova) await foreach (var item in client.LLM.BedrockNova.StreamAsync(source.Deserialize<BedrockNovaRequest>()!, new() { SessionId = "bedrock-stream" }, token)) seen.Add(item);
        else await foreach (var item in client.LLM.BedrockConverse.StreamAsync(source.Deserialize<BedrockConverseRequest>()!, new() { SessionId = "bedrock-stream" }, token)) seen.Add(item);
        return seen;
    }
}
