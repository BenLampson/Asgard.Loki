using System.Text.Json;

namespace Asgard.Loki.Image.OpenAI.Internal;

/// <summary>原生图像事件的纯字段校验；不保存生成/编辑状态，不执行关联推导或能力调度。</summary>
internal static class ImageSseData
{
    /// <summary>只校验已知事件结构，不臆造输出关联、partial 数量承诺或用量加法。</summary>
    internal static bool Validate(JsonElement data, string name, string family)
    {
        if (name != family + ".partial_image" && name != family + ".completed") return false;
        if (!data.TryGetProperty("b64_json", out var image) || image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString()))
            throw new InvalidDataException("原生图片事件缺少 b64_json。");
        if (name == family + ".partial_image"
            && (!data.TryGetProperty("partial_image_index", out var index) || !ImageJsonNumber.IsNonNegativeInteger(index)))
            throw new InvalidDataException("原生部分图像索引必须为非负整数。");
        foreach (var field in new[] { "background", "output_format", "quality", "size" })
            if (data.TryGetProperty(field, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("已知原生图片属性必须为字符串。");
        if (data.TryGetProperty("created_at", out var created) && !ImageJsonNumber.IsNonNegativeInteger(created))
            throw new InvalidDataException("图像事件 created_at 必须为非负整数。");
        if (data.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            if (usage.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生图片用量必须为对象。");
            foreach (var field in new[] { "input_tokens", "output_tokens", "total_tokens" })
                if (usage.TryGetProperty(field, out var count) && !ImageJsonNumber.IsNonNegativeInteger(count))
                    throw new InvalidDataException("原生图片用量计数必须为非负整数。");
            if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind != JsonValueKind.Null)
            {
                if (details.ValueKind != JsonValueKind.Object) throw new InvalidDataException("原生图片输入用量明细必须为对象。");
                foreach (var field in new[] { "text_tokens", "image_tokens" })
                    if (details.TryGetProperty(field, out var count) && !ImageJsonNumber.IsNonNegativeInteger(count))
                        throw new InvalidDataException("原生图片输入用量计数必须为非负整数。");
            }
        }
        return name == family + ".completed";
    }
}
