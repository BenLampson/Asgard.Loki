using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Tencent.Internal;

/// <summary>原生请求与完整 Response 形状校验，不做有损 DTO 投影或工具参数重组。</summary>
internal static class TencentPayload
{
    /// <summary>保留完整原生结构，冻结并检查实际字节后才允许取得签名凭据。</summary>
    internal static byte[] Snapshot(JsonObject request, string model, bool stream, int maximum)
    {
        ArgumentNullException.ThrowIfNull(request); TencentJson.Input(request);
        foreach (var property in request)
            if (property.Key.Equals("Model", StringComparison.OrdinalIgnoreCase) && property.Key != "Model" || property.Key.Equals("Stream", StringComparison.OrdinalIgnoreCase) && property.Key != "Stream"
                || new[] { "Action", "Version", "Region", "Timestamp", "Token", "SecretId", "SecretKey", "Authorization" }.Contains(property.Key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("原生字段不能覆盖大小写模型/流式控制或传输认证参数。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request); if (bytes.Length > maximum) throw new ArgumentException("混元原生请求超过字节上限。"); var data = TencentJson.Parse(bytes);
        if (!data.TryGetProperty("Model", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() != model) throw new ArgumentException("原生 Model 必须与已绑定实际模型一致。");
        if (!data.TryGetProperty("Stream", out value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != stream) throw new ArgumentException("原生 Stream 必须与调用方法一致。");
        if (!data.TryGetProperty("Messages", out value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) throw new ArgumentException("混元原生调用要求非空 Messages 数组。");
        foreach (var message in value.EnumerateArray())
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("Role", out var role) || role.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(role.GetString())) throw new ArgumentException("原生消息要求非空 Role，不隐式生成角色。");
        return bytes;
    }
    /// <summary>腾讯云 Error 字符串 Code 与 ErrorMsg 数字 Code 保留原始 JSON，不互相强制转换。</summary>
    internal static bool Error(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return false;
        return HasError(data) || data.TryGetProperty("Response", out var response) && response.ValueKind == JsonValueKind.Object && HasError(response);
    }
    /// <summary>只识别当前原生根的错误，不把消息/工具参数中的业务字段当错误。</summary>
    private static bool HasError(JsonElement data)
    {
        if (data.TryGetProperty("Error", out var error) && error.ValueKind != JsonValueKind.Null) return true;
        if (!data.TryGetProperty("ErrorMsg", out error) || error.ValueKind == JsonValueKind.Null) return false;
        return error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("Code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var number) || number != 0;
    }
    /// <summary>完整结果必须保留原生信封、真实 ID、全部选择及各自的原生终态。</summary>
    internal static void Complete(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("Response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())
            || !response.TryGetProperty("Choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) throw new InvalidDataException("混元完整结果缺少原生信封、ID 或选择。");
        foreach (var choice in choices.EnumerateArray())
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("FinishReason", out var finish) || finish.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(finish.GetString())
                || !choice.TryGetProperty("Message", out var message) || message.ValueKind != JsonValueKind.Object) throw new InvalidDataException("混元原生选择缺少终态或 Message 对象。");
    }
}
