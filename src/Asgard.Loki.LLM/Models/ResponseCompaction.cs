namespace Asgard.Loki.LLM;

/// <summary>完整保留原生压缩窗口、加密项和统计，不把压缩结果注册为生成响应链。</summary>
public sealed class ResponseCompaction : LlmJsonResult
{
    /// <summary>验证压缩结果基本结构并复制 JSON 所有权。</summary>
    public ResponseCompaction(JsonElement data, LlmResultMetadata metadata) : base(data, metadata)
    {
        if (!Data.TryGetProperty("object", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "response.compaction"
            || !Data.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("上游没有返回有效的 Responses 压缩窗口。");
        if (Data.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("压缩结果包含上游错误，不能作为成功窗口使用。");
    }

    /// <summary>完整原生窗口，包含保留项和加密项；调用者应整体使用，不只挑选压缩项。</summary>
    public JsonElement Output => Data.GetProperty("output");
}
