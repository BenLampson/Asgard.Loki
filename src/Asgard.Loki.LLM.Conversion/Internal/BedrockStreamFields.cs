using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>原生转换流有限损耗诊断，不把未知业务 error/type 字段当作协议错误。</summary>
internal sealed class BedrockStreamFields(LlmConversionOptions options)
{
    private readonly List<LlmConversionDiagnostic> _diagnostics = [];
    /// <summary>每次交付独立只读诊断数组。</summary>
    internal IReadOnlyList<LlmConversionDiagnostic> Diagnostics => Array.AsReadOnly(_diagnostics.ToArray());
    /// <summary>只复制确认支持的字段，未知字段包含 null 也记录精确原生路径。</summary>
    internal JsonObject Pick(JsonObject source, string path, params string[] fields)
    {
        foreach (var field in source) if (!fields.Contains(field.Key, StringComparer.Ordinal)) Loss(path + "." + field.Key);
        JsonObject target = [];
        foreach (var field in fields) ConversionJson.Copy(source, field, target, field);
        return target;
    }
    /// <summary>超限不删除旧诊断；明确失败并禁止成功收尾。</summary>
    internal void Loss(string path, string code = "unsupported_field")
    {
        if (_diagnostics.Count >= (options.MaxBedrockStreamDiagnostics ?? 1024)) BedrockConversion.Reject("$event", "bedrock_stream_diagnostic_limit");
        _diagnostics.Add(new(code, path, "目标原生协议未证明可等价表达此字段或事件。"));
    }
    /// <summary>只有明确的普通字段损耗许可可以继续，源状态/工具/资源硬失败不经过此分支。</summary>
    internal void Enforce()
    {
        if (_diagnostics.Count != 0 && !options.AllowLoss) throw new LlmConversionException(_diagnostics);
    }
}
