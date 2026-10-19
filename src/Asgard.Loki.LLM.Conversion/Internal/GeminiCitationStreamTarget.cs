using System.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>目标 Gemini 单文本累计约定的引用布局与输出膨胀上限，不聚合原生供应商状态。</summary>
internal static class GeminiCitationStreamTarget
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    /// <summary>有引用时只允许一个原生文本块，不能把多 part 或工具混排的引用重新标成 part 零。</summary>
    internal static void RequireLayout(ConversionDocument document)
    {
        var blocks = document.Messages.Single().Blocks;
        if (blocks.Any(block => block["citations"] is JsonArray { Count: > 0 })
            && (blocks.Count != 1 || Text(blocks[0]["type"]) != "text"))
            throw new LlmConversionException([new("grounding_stream_layout_not_representable", "$event.response.output",
                "目标单文本 grounding 流不能保真表达多文本块或工具混排引用。")]);
    }
    /// <summary>输出的来源与支持共享独立条目/字节额度，输入引用已受自己的状态额度约束。</summary>
    internal static void RequireLimits(JsonObject complete, int maximumCount, int maximumBytes)
    {
        if (complete["candidates"]?[0]?["groundingMetadata"] is not JsonObject metadata) return;
        long bytes = 0; var count = 0;
        foreach (var value in new[] { "groundingChunks", "groundingSupports" }.SelectMany(name => Array(metadata[name])))
        {
            if (++count > maximumCount) Limit("stream_grounding_count_limit");
            bytes += StrictUtf8.GetByteCount(value!.ToJsonString());
            if (bytes > maximumBytes) Limit("stream_grounding_byte_limit");
        }
    }
    /// <summary>资源限额不能由普通损耗许可改成截断来源或支持列表。</summary>
    private static void Limit(string code) => throw new LlmConversionException([new(code,
        "$event.candidates[].groundingMetadata", "目标 grounding 超过调用者配置的单次流资源上限。")]);
}
