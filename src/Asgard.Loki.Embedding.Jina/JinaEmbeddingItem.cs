using System.Text.Json;
using Asgard.Loki.Embedding.Jina.Internal;

namespace Asgard.Loki.Embedding.Jina;

/// <summary>保存原生单/多向量项及其输入索引，不把 token 维度或输入维度混成一个数组。</summary>
public sealed class JinaEmbeddingItem
{
    /// <summary>验证原生 item 后保存独立完整快照。</summary>
    public JinaEmbeddingItem(JsonElement data) { _ = JinaEmbeddingItemCodec.Read(data); Data = data.Clone(); }
    /// <summary>完整原生 item，包含未知字段与显式 null。</summary>
    public JsonElement Data { get; }
    /// <summary>原始输入项索引，不是返回数组位置或 token 序号。</summary>
    public int Index { get { _ = JinaEmbeddingNumber.TryInt32(Data.GetProperty("index"), out var index); return index; } }
    /// <summary>由明确原生 embeddings 字段识别，不按模型名称猜测。</summary>
    public bool IsMultiVector => Data.TryGetProperty("embeddings", out _);
    /// <summary>单向量原文；多向量时为空，不自动解码。</summary>
    public JsonElement? Embedding => Data.TryGetProperty("embedding", out var value) ? value : null;
    /// <summary>多向量原文矩阵/字符串数组；单向量时为空，不 flatten。</summary>
    public JsonElement? Embeddings => Data.TryGetProperty("embeddings", out var value) ? value : null;
    /// <summary>单项数值向量的独立便利视图；Base64 或多向量时为空。</summary>
    public IReadOnlyList<double>? Values => Embedding is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().Select(v => v.GetDouble()).ToArray() : null;
    /// <summary>单项 Base64 原文；其他外壳为空。</summary>
    public string? Base64 => Embedding is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    /// <summary>多向量返回的对应 token 字符串；缺失/null 不补造，原文区别保留在 Data。</summary>
    public IReadOnlyList<string>? TokenizedInput => IsMultiVector && Data.TryGetProperty("tokenized_input", out var tokens) && tokens.ValueKind == JsonValueKind.Array
        ? tokens.EnumerateArray().Select(t => t.GetString()!).ToArray() : null;
    /// <summary>显式解码单项原生 Base64 字节，不解释浮点端序或替换原 JSON。</summary>
    public ReadOnlyMemory<byte> DecodeBytes() => Base64 is { } text ? Convert.FromBase64String(text)
        : throw new InvalidOperationException("当前项不是单项 Base64 向量。");
}
