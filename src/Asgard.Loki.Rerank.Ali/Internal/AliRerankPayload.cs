using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Rerank.Ali.Internal;

/// <summary>保持 DashScope 原生嵌套外壳、文档身份和明确返回选项，不注入默认参数。</summary>
internal sealed class AliRerankPayload(JsonObject body, JsonElement documents, int count, bool returnDocuments)
{
    /// <summary>固定实际模型的独立正文。</summary>
    internal JsonObject Body { get; } = body;
    /// <summary>对应本次请求的原始文档快照。</summary>
    internal JsonElement Documents { get; } = documents;
    /// <summary>省略 top_n 时为全部文档，正数上限不超过输入数量。</summary>
    internal int ExpectedCount { get; } = count;
    /// <summary>是否明确要求返回文档；不把原生默认 false 写入请求。</summary>
    internal bool RequireDocuments { get; } = returnDocuments;

    /// <summary>直接调用也先复制参数；不把扁平的 SDK 方言冒充原生 HTTP 请求。</summary>
    internal static AliRerankPayload Create(RerankRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("原生重排参数不能覆盖公共 model 或包含 Undefined。");
        var values = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!values.TryGetValue("input", out var input) || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("query", out var query) || !AliRerankDocument.TryContent(query, out _, out _, query: true)
            || !input.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0)
            throw new ArgumentException("DashScope 需要原生 input.query 和非空 input.documents。");
        foreach (var document in documents.EnumerateArray())
            if (!AliRerankDocument.TryContent(document, out _, out _)) throw new ArgumentException("文档必须为原生文本、图片或视频项。");
        if (new[] { "query", "documents", "top_n", "return_documents", "instruct", "fps" }.Any(values.ContainsKey))
            throw new ArgumentException("已知输入及选项必须放在 DashScope 原生 input/parameters 中。");
        var count = documents.GetArrayLength(); var returned = false;
        if (values.TryGetValue("parameters", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("parameters 必须为原生对象。");
            if (parameters.TryGetProperty("top_n", out var top))
            {
                if (!AliRerankNumber.IsNonNegativeInteger(top, positive: true)) throw new ArgumentException("top_n 必须为正整数。");
                if (AliRerankNumber.TryInt32(top, out var limit)) count = Math.Min(count, limit);
            }
            if (parameters.TryGetProperty("return_documents", out var flag))
            {
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("return_documents 必须为布尔值。");
                returned = flag.GetBoolean();
            }
            if (parameters.TryGetProperty("instruct", out var instruction) && instruction.ValueKind != JsonValueKind.String)
                throw new ArgumentException("instruct 必须为字符串。");
            if (parameters.TryGetProperty("fps", out var fps) && !AliRerankNumber.TryScore(fps, out _))
                throw new ArgumentException("fps 必须为零至一的有限 JSON 数值。");
        }
        var body = JsonSerializer.SerializeToNode(values)!.AsObject(); body["model"] = model;
        return new(body, documents, count, returned);
    }
}
