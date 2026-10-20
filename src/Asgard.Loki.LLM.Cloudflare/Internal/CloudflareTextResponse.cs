using System.Text.Json;

namespace Asgard.Loki.LLM.Cloudflare.Internal;

/// <summary>只识别原生文本结果与显式错误，不转换角色、工具关联或使用量。</summary>
internal static class CloudflareTextResponse
{
    /// <summary>原生根错误、失败 success 或非空 errors 保持其实际错误含义，不误判嵌套业务字段。</summary>
    internal static bool Error(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (CloudflareJson.Error(data) || data.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return true;
        if (data.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null)
        {
            if (errors.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Workers AI 原生 errors 必须为数组。");
            return errors.GetArrayLength() != 0;
        }
        return false;
    }
    /// <summary>完整结果必须保留真实原生封装与文本分支，不把其他 ai/run 模型格式猜测为 Chat。</summary>
    internal static void Complete(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
            || !data.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Workers AI 原生文本结果缺少成功封装与 result.response 字符串。");
        Record(result);
    }
    /// <summary>只验证已知文本/工具容器类型，原生 name/arguments/usage 的内容与未知字段均不重关联或归一化。</summary>
    internal static void Record(JsonElement data)
    {
        if (data.TryGetProperty("response", out var text) && text.ValueKind != JsonValueKind.String) throw new InvalidDataException("Workers AI 原生 response 必须是字符串。");
        if (data.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
        {
            if (calls.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Workers AI 原生 tool_calls 必须是数组。");
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Workers AI 原生工具调用必须为对象。");
                if (call.TryGetProperty("name", out var name) && name.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new InvalidDataException("Workers AI 原生工具名必须为字符串或 null。");
            }
        }
    }
}
