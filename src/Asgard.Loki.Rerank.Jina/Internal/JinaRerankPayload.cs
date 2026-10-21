using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Rerank.Jina.Internal;

/// <summary>快照完整原生参数和本次文档关联，不增加默认字段或根据模型名调整请求。</summary>
internal sealed class JinaRerankPayload
{
    private JinaRerankPayload(JsonObject body, JsonElement documents, int count, bool requireDocuments, bool requireEmbeddings)
    { Body = body; Documents = documents; ExpectedCount = count; RequireDocuments = requireDocuments; RequireEmbeddings = requireEmbeddings; }
    /// <summary>只将公共模型别名替换为实际模型的原生报文。</summary>
    internal JsonObject Body { get; }
    /// <summary>本次原始输入文档的独立快照。</summary>
    internal JsonElement Documents { get; }
    /// <summary>完整原生结果应覆盖的数量。</summary>
    internal int ExpectedCount { get; }
    /// <summary>省略选项时按原生文档默认值验证，显式 null 不擅自解释为 true。</summary>
    internal bool RequireDocuments { get; }
    /// <summary>仅在调用者显式请求向量时要求每项返回向量。</summary>
    internal bool RequireEmbeddings { get; }

    /// <summary>直接调用适配器时也验证和复制原生 JSON，不依赖核心路由预处理。</summary>
    internal static JinaRerankPayload Create(RerankRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生重排参数不能覆盖公共 model 或包含 Undefined。", nameof(request));
        var parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!parameters.TryGetValue("query", out var query) || !JinaRerankDocument.TryContent(query, out _, out _, query: true))
            throw new ArgumentException("Jina query 必须是原生字符串或 ImageDoc。");
        if (!parameters.TryGetValue("documents", out var documents) || documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0)
            throw new ArgumentException("Jina documents 必须是非空原生数组。");
        foreach (var document in documents.EnumerateArray())
            if (!JinaRerankDocument.TryContent(document, out _, out _)) throw new ArgumentException("Jina 文档必须是字符串、TextDoc 或 ImageDoc。");
        var count = documents.GetArrayLength();
        if (parameters.TryGetValue("top_n", out var top) && top.ValueKind != JsonValueKind.Null)
        {
            if (!JinaRerankNumber.IsNonNegativeInteger(top, positive: true)) throw new ArgumentException("top_n 必须是正整数或 null。");
            if (JinaRerankNumber.TryInt32(top, out var limit)) count = Math.Min(count, limit);
        }
        var returned = NullableBoolean(parameters, "return_documents", absent: true);
        var embeddings = NullableBoolean(parameters, "return_embeddings", absent: false);
        if (parameters.TryGetValue("max_doc_length", out var length) && length.ValueKind != JsonValueKind.Null
            && (!JinaRerankNumber.TryInt32(length, out var size) || size is < 1 or > 8192))
            throw new ArgumentException("原生 max_doc_length 必须是 1 至 8192 的整数或 null。");
        var body = JsonSerializer.SerializeToNode(parameters)!.AsObject(); body["model"] = model;
        return new(body, documents, count, returned, embeddings);
    }

    /// <summary>保留缺失、false 与 null 的差别，不把默认值写入调用报文。</summary>
    private static bool NullableBoolean(Dictionary<string, JsonElement> parameters, string name, bool absent)
    {
        if (!parameters.TryGetValue(name, out var value)) return absent;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            throw new ArgumentException($"{name} 必须是布尔值或 null。");
        return value.ValueKind == JsonValueKind.True;
    }
}
