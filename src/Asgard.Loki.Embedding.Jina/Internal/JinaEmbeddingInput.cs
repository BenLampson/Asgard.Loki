using System.Text.Json;

namespace Asgard.Loki.Embedding.Jina.Internal;

/// <summary>统计原生文本/媒体/融合输入，不拆组、下载或把文档对象转换为字符串。</summary>
internal static class JinaEmbeddingInput
{
    /// <summary>每个批条目对应一个结果，融合组仍占一项；空批次不伪造输入。</summary>
    internal static (int Count, bool TextOnly) Read(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array) return (1, Item(input, group: false));
        var textOnly = true;
        foreach (var item in input.EnumerateArray()) textOnly &= Item(item, group: true);
        return (input.GetArrayLength(), textOnly);
    }

    /// <summary>已知 oneof 不能混用，未知字段保留；原生内容串不修剪、判断 URL 或猜测 MIME。</summary>
    private static bool Item(JsonElement item, bool group)
    {
        if (item.ValueKind == JsonValueKind.String) return true;
        if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException("Jina input 必须为文本或原生文档对象。");
        var fields = new[] { "text", "image", "audio", "video", "pdf", "content" }.Where(n => item.TryGetProperty(n, out _)).ToArray();
        if (fields.Length != 1) throw new ArgumentException("Jina 原生输入项必须有一个明确的内容字段。");
        var name = fields[0]; var value = item.GetProperty(name);
        if (name == "pdf") throw new NotSupportedException("PDF 页/输入对应关系尚未核实，请勿推断结果数量或自动展开页面。");
        if (name != "content")
        {
            if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("原生内容字段必须为字符串。");
            return name == "text";
        }
        if (!group || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            throw new ArgumentException("融合 content 只能作为批内非空组。");
        var textOnly = true;
        foreach (var chunk in value.EnumerateArray())
        {
            if (chunk.ValueKind != JsonValueKind.Object) throw new ArgumentException("融合组只能包含原生模态对象。");
            textOnly &= Item(chunk, group: false);
        }
        return textOnly;
    }
}
