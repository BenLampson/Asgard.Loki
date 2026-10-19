using System.Text;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>多候选逻辑流的共享资源边界；计量不涉及价格、额度或供应商 token 统计。</summary>
internal sealed class CandidateStreamBudget(LlmConversionOptions options)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private long _inputBytes;
    private long _groundingBytes;
    private int _groundingCount;
    /// <summary>累计源逻辑 JSON 字节数，先验证 Unicode，再允许状态保存本帧内容。</summary>
    internal void Input(JsonObject source)
    {
        ValidateStrings(source);
        var bytes = Size(source);
        if (bytes > (options.MaxCandidateStreamInputBytes ?? 16 * 1024 * 1024) - _inputBytes) Reject("candidate_stream_input_limit");
        _inputBytes += bytes;
        if (source["candidates"] is not JsonArray candidates) return;
        foreach (var candidate in candidates)
            if (candidate?["groundingMetadata"] is JsonObject metadata)
                foreach (var field in new[] { "groundingChunks", "groundingSupports" })
                    if (metadata[field] is JsonArray entries)
                        foreach (var entry in entries)
                        {
                            _groundingCount++; _groundingBytes += Size(entry);
                            if (_groundingCount > options.MaxStreamAnnotations) Reject("stream_annotation_count_limit");
                            if (_groundingBytes > options.MaxStreamAnnotationBytes) Reject("stream_annotation_byte_limit");
                        }
    }
    /// <summary>每个返回结果都会独立保留源快照，防止多候选放大元数据复制而绕过批次上限。</summary>
    internal void Output(IReadOnlyList<JsonObject> output, JsonObject source)
    {
        long total = 0; var original = Size(source);
        foreach (var item in output)
        {
            total += original + Size(item);
            if (total > (options.MaxCandidateStreamOutputBytes ?? 4 * 1024 * 1024)) Reject("candidate_stream_output_limit");
        }
    }
    /// <summary>所有候选及公共信封共享一个诊断总量，不按候选倍增额度。</summary>
    internal void Diagnostics(int count)
    { if (count > (options.MaxCandidateStreamDiagnostics ?? 1024)) Reject("candidate_stream_diagnostic_limit"); }
    /// <summary>计数针对本地逻辑 JSON 的紧凑序列化，不冒充原始 SSE 字节。</summary>
    private static int Size(JsonNode? value) => StrictUtf8.GetByteCount(value?.ToJsonString() ?? "null");
    /// <summary>原始字符串不能通过 JSON 序列化的替换字符悄悄改变内容。</summary>
    internal static void ValidateStrings(JsonNode source)
    {
        Stack<JsonNode> pending = new(); pending.Push(source);
        while (pending.TryPop(out var node))
        {
            if (node is JsonObject obj)
                foreach (var item in obj) { _ = StrictUtf8.GetByteCount(item.Key); if (item.Value is not null) pending.Push(item.Value); }
            else if (node is JsonArray array)
            { foreach (var item in array) if (item is not null) pending.Push(item); }
            else if (node is JsonValue value && value.TryGetValue<string>(out var text)) _ = StrictUtf8.GetByteCount(text);
        }
    }
    /// <summary>越界是硬失败，不因 AllowLoss 截断参数、候选、元数据或诊断。</summary>
    internal static void Reject(string code) => throw new LlmConversionException([new(code, "$stream", "多候选转换流超过显式资源边界。")]);
}
