using System.Text.Json;

namespace Asgard.Loki.Rerank.SGLang.Internal;

/// <summary>校验并快照原生请求，不将公共路由或目标模型注入没有 model 字段的协议。</summary>
internal static class SgLangRerankPayload
{
    /// <summary>保留文本/内容块的原始边界、顺序、空值和未知字段。</summary>
    internal static (JsonElement Body, JsonElement Documents, int ExpectedCount, bool ReturnDocuments) Create(RerankRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model); SgLangRerankJson.Text(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        foreach (var field in request.Parameters)
        {
            SgLangRerankJson.Text(field.Key); SgLangRerankJson.Validate(field.Value);
            if (field.Key.Equals("model", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("SGLang 原生重排参数不能覆盖公共 model。");
        }
        var body = JsonSerializer.SerializeToElement(request.Parameters);
        if (!body.TryGetProperty("query", out var query)) throw new ArgumentException("重排必须包含原生 query。");
        Content(query);
        if (!body.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() == 0)
            throw new ArgumentException("documents 必须为非空原生数组。");
        foreach (var document in documents.EnumerateArray()) Content(document);
        var expected = documents.GetArrayLength();
        if (body.TryGetProperty("top_n", out var top) && top.ValueKind != JsonValueKind.Null)
        {
            if (!SgLangRerankNumber.IsNonNegativeInteger(top, positive: true)) throw new ArgumentException("top_n 必须为空或正整数。");
            if (SgLangRerankNumber.TryInt32(top, out var n)) expected = Math.Min(expected, n);
        }
        var returnDocuments = true;
        if (body.TryGetProperty("return_documents", out var returned))
        {
            if (returned.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("return_documents 必须为布尔值。");
            returnDocuments = returned.GetBoolean();
        }
        if (body.TryGetProperty("instruct", out var instruct) && instruct.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ArgumentException("instruct 必须为原生字符串或空值。");
        if (body.TryGetProperty("stream", out var stream) && stream.ValueKind != JsonValueKind.False) throw new ArgumentException("SGLang 重排不支持流式请求。");
        return (body, documents, expected, returnDocuments);
    }
    /// <summary>只接受原生文本或文本/图片/视频块；不取媒体、不拼接文本或执行模板。</summary>
    private static void Content(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        { if (string.IsNullOrWhiteSpace(value.GetString())) throw new ArgumentException("重排查询和文档不能是空白文本。"); return; }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) throw new ArgumentException("重排内容必须为非空文本或原生内容块数组。");
        foreach (var part in value.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new ArgumentException("内容块缺少原生 type。");
            var kind = type.GetString();
            if (kind == "text")
            { if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new ArgumentException("text 块缺少字符串 text。"); }
            else if (kind is "image_url" or "video_url")
            { if (!part.TryGetProperty(kind, out var media) || media.ValueKind != JsonValueKind.Object || !media.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) throw new ArgumentException("媒体块缺少原生 url 字符串。"); }
            else throw new ArgumentException("SGLang 重排不支持该原生内容块类型。");
        }
    }
}
