using System.Text.Json;

namespace Asgard.Loki.Rerank.OpenAICompatible.Internal;

/// <summary>快照显式原生兼容请求，只映射固定目标模型，不转换候选文档或注入选项。</summary>
internal static class OpenAiCompatibleRerankPayload
{
    /// <summary>保留原始 JSON 候选、未知字段、空值和精确数字；模型/语义接受度由所选服务决定。</summary>
    internal static (JsonElement Body, JsonElement Documents, int ExpectedCount, bool ReturnDocuments) Create(RerankRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); OpenAiCompatibleRerankJson.Text(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        foreach (var field in request.Parameters)
        {
            OpenAiCompatibleRerankJson.Text(field.Key); OpenAiCompatibleRerankJson.Validate(field.Value);
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("原生参数不能覆盖公共 model。");
        }
        var fields = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal); fields.Add("model", JsonSerializer.SerializeToElement(model));
        var body = JsonSerializer.SerializeToElement(fields);
        if (!body.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String) throw new ArgumentException("兼容重排需要原生字符串 query。");
        if (!body.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0)
            throw new ArgumentException("documents 必须为非空原生候选数组。");
        var expected = documents.GetArrayLength();
        if (body.TryGetProperty("top_n", out var top) && top.ValueKind != JsonValueKind.Null)
        {
            if (!OpenAiCompatibleRerankNumber.IsNonNegativeInteger(top, positive: true)) throw new ArgumentException("top_n 必须为空或精确正整数。");
            if (OpenAiCompatibleRerankNumber.TryInt32(top, out var n)) expected = Math.Min(expected, n);
        }
        var returnDocuments = false;
        if (body.TryGetProperty("return_documents", out var returned))
        {
            if (returned.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) throw new ArgumentException("return_documents 必须为布尔或空值。");
            returnDocuments = returned.ValueKind == JsonValueKind.True;
        }
        if (body.TryGetProperty("stream", out var stream) && stream.ValueKind != JsonValueKind.False) throw new ArgumentException("兼容重排不支持流式请求。");
        return (body, documents, expected, returnDocuments);
    }
}
