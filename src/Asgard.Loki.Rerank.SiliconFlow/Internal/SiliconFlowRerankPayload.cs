using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Rerank.SiliconFlow.Internal;

/// <summary>保存原生请求及原始文档快照，线路上的单字符串/数组不会互相改写。</summary>
internal sealed class SiliconFlowRerankPayload(JsonObject body, JsonElement documents, int expectedCount, bool requireDocuments)
{
    /// <summary>唯一替换实际模型的完整原生报文。</summary>
    internal JsonObject Body { get; } = body;
    /// <summary>内部关联使用的索引快照；单字符串只在私有上下文中对应索引零。</summary>
    internal JsonElement Documents { get; } = documents;
    /// <summary>依据原始候选数和显式 top_n 的完整排名数量。</summary>
    internal int ExpectedCount { get; } = expectedCount;
    /// <summary>仅显式 true 要求每项存在原文档；不把默认 false 写进请求。</summary>
    internal bool RequireDocuments { get; } = requireDocuments;

    /// <summary>直接调用适配器也先完成独立快照和结构校验，不按模型名过滤或注入参数。</summary>
    internal static SiliconFlowRerankPayload Create(RerankRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生参数不能覆盖公共 model 或包含 Undefined。", nameof(request));
        var parameters = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!parameters.TryGetValue("query", out var query) || !SiliconFlowRerankDocument.TryContent(query, out _, out _, query: true))
            throw new ArgumentException("SiliconFlow 查询必须为非空文本或原生图片对象。");
        if (!parameters.TryGetValue("documents", out var input) || input.ValueKind is not (JsonValueKind.Array or JsonValueKind.String)
            || input.ValueKind == JsonValueKind.Array && input.GetArrayLength() == 0)
            throw new ArgumentException("SiliconFlow 文档必须为原生字符串或非空文档数组。");
        var documents = input.ValueKind == JsonValueKind.String ? JsonSerializer.SerializeToElement(new[] { input.GetString()! }) : input;
        foreach (var item in documents.EnumerateArray())
            if (!SiliconFlowRerankDocument.TryContent(item, out _, out _)) throw new ArgumentException("重排文档必须为原生字符串、文本或图片对象。");
        var count = documents.GetArrayLength();
        foreach (var name in new[] { "top_n", "max_chunks_per_doc" })
            if (parameters.TryGetValue(name, out var value) && !SiliconFlowRerankNumber.IsInteger(value, positive: true))
                throw new ArgumentException($"{name} 必须为原生正整数。");
        if (parameters.TryGetValue("top_n", out var top) && SiliconFlowRerankNumber.TryInt32(top, out var number)) count = Math.Min(count, number);
        if (parameters.TryGetValue("overlap_tokens", out var overlap)
            && (!SiliconFlowRerankNumber.TryInt32(overlap, out var tokens) || tokens is < 0 or > 80))
            throw new ArgumentException("overlap_tokens 必须为 0 至 80 的原生整数。");
        if (parameters.TryGetValue("instruction", out var instruction) && (instruction.ValueKind != JsonValueKind.String || instruction.GetString()!.Length == 0))
            throw new ArgumentException("instruction 必须为非空原生字符串。");
        var returned = parameters.TryGetValue("return_documents", out var flag);
        if (returned && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("return_documents 必须为布尔值。");
        var native = JsonSerializer.SerializeToNode(parameters)!.AsObject(); native["model"] = model;
        return new(native, documents, count, returned && flag.GetBoolean());
    }
}
