using System.Text.Json;

namespace Asgard.Loki.Rerank.SGLang.Internal;

/// <summary>验证原生分数数组、索引及文档回显，不重排结果或伪造用量。</summary>
internal static class SgLangRerankResponse
{
    /// <summary>原生错误对象和常见包裹错误均不能成为数组成功。</summary>
    internal static bool IsError(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && (root.TryGetProperty("object", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "error"
            || root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null);
    /// <summary>保留原数组次序；有请求上下文时严格验证返回数量、唯一范围和文档关联。</summary>
    internal static void Validate(JsonElement root, JsonElement? documents = null, int? expected = null, bool requireDocuments = false)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || expected is { } count && root.GetArrayLength() != count)
            throw new InvalidDataException("SGLang 必须返回完整原生排序数组，数量与本次 top_n/输入关联。");
        var seen = new HashSet<int>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var identity)
                || !SgLangRerankNumber.TryInt32(identity, out var index) || index < 0 || !seen.Add(index)
                || documents is { } originals && index >= originals.GetArrayLength()
                || !item.TryGetProperty("score", out var score) || score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var number) || !double.IsFinite(number))
                throw new InvalidDataException("SGLang 重排条目缺少有限 score 或精确、唯一且属于本次输入的 index。");
            if (item.TryGetProperty("meta_info", out var meta) && meta.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)) throw new InvalidDataException("meta_info 必须为原生对象或空值。");
            var echoed = item.TryGetProperty("document", out var document) && document.ValueKind != JsonValueKind.Null;
            if (echoed && document.ValueKind != JsonValueKind.String) throw new InvalidDataException("document 回显必须为原生字符串。");
            if (documents is not { } source) continue;
            if (!echoed && requireDocuments) throw new InvalidDataException("SGLang 遗漏原生返回约定要求的文档回显。");
            if (echoed && (source[index].ValueKind != JsonValueKind.String || !string.Equals(source[index].GetString(), document.GetString(), StringComparison.Ordinal)))
                throw new InvalidDataException("回显文档与原始索引内容不关联，不能自行渲染或补造。");
        }
    }
}
