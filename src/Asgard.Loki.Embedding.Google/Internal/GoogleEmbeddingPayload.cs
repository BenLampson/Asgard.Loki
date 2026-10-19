using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.Embedding.Google.Internal;

/// <summary>保持 Google 原生 Content 和配置外壳，只绑定固定模型和批请求的对应维度。</summary>
internal static class GoogleEmbeddingPayload
{
    /// <summary>批操作由配置确定；省略的批内 model 使用固定目标，显式冲突或 null 均拒绝。</summary>
    internal static (JsonObject Body, int?[] Dimensions) Create(EmbeddingRequest request, GoogleEmbeddingOperation operation, string resource)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        if (request.Parameters.Any(p => p.Key == "model" || p.Value.ValueKind == JsonValueKind.Undefined))
            throw new ArgumentException("Google 嵌入扩展字段不能覆盖公共 model 或包含 Undefined。");
        var snapshot = request.Parameters.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
        var body = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
        if (operation == GoogleEmbeddingOperation.EmbedContent)
        {
            if (body.ContainsKey("requests")) throw new ArgumentException("单条嵌入不能携带批 requests。");
            return (body, [Content(body)]);
        }
        if (body.ContainsKey("content")) throw new ArgumentException("批嵌入必须把 Content 放在各 requests 中。");
        if (body["requests"] is not JsonArray { Count: > 0 } batch) throw new ArgumentException("批嵌入需要非空 requests 数组。");
        var dimensions = new int?[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            if (batch[i] is not JsonObject item) throw new ArgumentException("批内嵌入请求必须为对象。");
            if (!item.ContainsKey("model")) item["model"] = resource;
            else if (item["model"] is not JsonValue model || model.GetValueKind() != JsonValueKind.String || model.GetValue<string>() != resource)
                throw new ArgumentException("批内模型必须与完整固定目标匹配。");
            dimensions[i] = Content(item);
        }
        return (body, dimensions);
    }

    /// <summary>只验证 Content 容器；不按模型限制文本/媒体类型，也不归一化任务或自动下载文件。</summary>
    private static int? Content(JsonObject request)
    {
        if (request["content"] is not JsonObject content || content["parts"] is not JsonArray { Count: > 0 } parts)
            throw new ArgumentException("Google 嵌入需要具有非空 parts 的原生 content。");
        foreach (var part in parts)
            if (part is not JsonObject { Count: > 0 }) throw new ArgumentException("Content part 必须是非空原生对象。");
        return GoogleEmbeddingDimension.Read(request);
    }
}
