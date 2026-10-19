using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>保存一次纯数据转换的独立结果、原始数据与损耗诊断。</summary>
public sealed class LlmConversionResult
{
    /// <summary>内部创建已完成的转换快照。</summary>
    internal LlmConversionResult(JsonObject data, JsonObject original, IEnumerable<LlmConversionDiagnostic> diagnostics, LlmConversionContext? context = null)
    {
        Data = JsonSerializer.SerializeToElement(data);
        OriginalData = JsonSerializer.SerializeToElement(original);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        Context = context;
    }
    /// <summary>目标协议 JSON。</summary>
    public JsonElement Data { get; }
    /// <summary>未经改写的源协议 JSON。</summary>
    public JsonElement OriginalData { get; }
    /// <summary>非等价字段的完整诊断。</summary>
    public IReadOnlyList<LlmConversionDiagnostic> Diagnostics { get; }
    /// <summary>请求及使用其上下文还原的响应/事件携带的不可变状态；独立响应/事件转换为 null。</summary>
    public LlmConversionContext? Context { get; }
}
