using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>先搜索语义的独立完整原生请求和预期报文，不使用生产写入器生成金样。</summary>
internal static class ChatSearchFixtures
{
    /// <summary>读取独立样本，保留 Chat 嵌套位置与对端扁平位置的不同结构。</summary>
    internal static JsonObject Read(LlmProtocol protocol, bool expected = false)
    {
        using var stream = typeof(ChatSearchFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.search.chat-required.json") ?? throw new InvalidDataException("Chat 搜索样本缺失。");
        return JsonNode.Parse(stream)![(expected ? "Expected" : "") + protocol]!.AsObject();
    }

    /// <summary>四个保留显式搜索要求的已验证方向。</summary>
    public static IEnumerable<object[]> Directions()
    {
        foreach (var peer in new[] { LlmProtocol.Responses, LlmProtocol.ClaudeMessages })
        { yield return [LlmProtocol.ChatCompletions, peer]; yield return [peer, LlmProtocol.ChatCompletions]; }
    }

    /// <summary>调用者确认固定目标的原生搜索能力；不按模型名猜测。</summary>
    internal static LlmConversionOptions Options(LlmProtocol target) => target == LlmProtocol.ChatCompletions
        ? new() { HostedSearchTarget = LlmHostedSearchTarget.ChatWebSearch } : HostedSearchFixtures.Options(target);

    /// <summary>原生配置对象的位置。</summary>
    internal static JsonObject Settings(JsonObject request, LlmProtocol protocol)
        => (protocol == LlmProtocol.ChatCompletions ? request["web_search_options"] : request["tools"]![0])!.AsObject();

    /// <summary>显式转换快捷入口。</summary>
    internal static LlmConversionResult Convert(JsonObject source, LlmProtocol from, LlmProtocol to, LlmConversionOptions? options = null)
        => LlmProtocolConverter.ConvertRequest(source, from, to, options ?? Options(to));
}
