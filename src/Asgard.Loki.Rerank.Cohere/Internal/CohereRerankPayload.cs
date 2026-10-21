using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Rerank.Cohere.Internal;

/// <summary>保存独立原生请求和关联上下文，不转文本、拆分文档、截断或注入默认选项。</summary>
internal sealed class CohereRerankPayload
{
    private CohereRerankPayload(JsonObject body, JsonElement documents, int count, bool requireDocuments)
    { Body = body; Documents = documents; ExpectedCount = count; RequireDocuments = requireDocuments; }

    /// <summary>只替换目标模型的完整原生请求。</summary>
    internal JsonObject Body { get; }
    /// <summary>本次输入的独立文档快照，索引关联使用原始数组。</summary>
    internal JsonElement Documents { get; }
    /// <summary>本次必须返回的结果数量，不把部分正文当作完整成功。</summary>
    internal int ExpectedCount { get; }
    /// <summary>v1 显式要求返回原文档时必须校验文档存在。</summary>
    internal bool RequireDocuments { get; }

    /// <summary>直接调用适配器时也建立快照和固定版本校验，不依赖核心路由替供应商验证。</summary>
    internal static CohereRerankPayload Create(RerankRequest request, string model, CohereRerankApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生重排参数不能覆盖公共 model 或包含 Undefined。", nameof(request));
        var parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!parameters.TryGetValue("query", out var query) || query.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Cohere 重排需要字符串 query。");
        if (!parameters.TryGetValue("documents", out var documents) || documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0)
            throw new ArgumentException("Cohere 重排需要非空 documents 数组。");
        foreach (var document in documents.EnumerateArray())
        {
            if (document.ValueKind == JsonValueKind.String) continue;
            if (version != CohereRerankApiVersion.V1 || document.ValueKind != JsonValueKind.Object
                || !document.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new ArgumentException("v1 文档对象必须含字符串 text，v2 仅接受字符串文档；SDK 不自动转换格式。");
        }
        var count = documents.GetArrayLength();
        if (parameters.TryGetValue("top_n", out var top)) count = Math.Min(count, Integer(top, "top_n", 1));
        var returnDocuments = false;
        if (version == CohereRerankApiVersion.V1)
        {
            if (parameters.TryGetValue("return_documents", out var returned))
            {
                if (returned.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("return_documents 必须是布尔值。");
                returnDocuments = returned.GetBoolean();
            }
            if (parameters.TryGetValue("rank_fields", out var fields)
                && (fields.ValueKind != JsonValueKind.Array || fields.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.String)))
                throw new ArgumentException("rank_fields 必须是原生字符串数组。");
            if (parameters.TryGetValue("max_chunks_per_doc", out var chunks)) _ = Integer(chunks, "max_chunks_per_doc", 1);
        }
        else
        {
            if (parameters.TryGetValue("max_tokens_per_doc", out var tokens)) _ = Integer(tokens, "max_tokens_per_doc", 1);
            if (parameters.TryGetValue("priority", out var priority)) _ = Integer(priority, "priority", 0, 999);
        }
        var body = JsonSerializer.SerializeToNode(parameters)!.AsObject(); body["model"] = model;
        return new(body, documents, count, returnDocuments);
    }

    /// <summary>校验原生整数配置，不四舍五入或修复调用者选项。</summary>
    private static int Integer(JsonElement value, string name, int minimum, int maximum = int.MaxValue)
        => CohereRerankNumber.TryInt32(value, out var number) && number >= minimum && number <= maximum ? number
            : throw new ArgumentException($"{name} 必须是范围内的原生整数。");
}
