using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立手写的完整原生工具流和 Chat 快照，不从转换器生成预期数据。</summary>
internal static class TypedStreamErrorFixtures
{
    /// <summary>每次返回独立可变副本，避免测试间共享流状态。</summary>
    internal static JsonArray Read(string name)
    {
        using var stream = typeof(TypedStreamErrorFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.typed-stream-errors.json")!;
        return JsonNode.Parse(stream)![name]!.AsArray();
    }

    /// <summary>原始调用 ID 与原始工具参数分片必须保留。</summary>
    internal static List<JsonObject> Source(LlmProtocol protocol, bool unknown = true)
    {
        var source = Read(protocol.ToString()).Select(item => item!.AsObject()).ToList();
        if (!unknown) source.RemoveAt(1);
        return source;
    }

    /// <summary>正常转换仍必须等待调用者确认源枚举与资源释放已完成。</summary>
    internal static List<LlmConversionResult> Convert(LlmProtocol protocol, IEnumerable<JsonObject> source, bool allowLoss)
    {
        var converter = new LlmProtocolStreamConverter(protocol, LlmProtocol.ChatCompletions, new() { AllowLoss = allowLoss });
        List<LlmConversionResult> results = [];
        foreach (var item in source) results.AddRange(converter.Push(item));
        results.AddRange(converter.Complete()); return results;
    }
}
