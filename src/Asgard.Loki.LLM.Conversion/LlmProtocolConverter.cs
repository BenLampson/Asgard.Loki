using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion.Internal;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>显式、无网络的协议数据转换入口；同协议转换完整复制原文。</summary>
public static class LlmProtocolConverter
{
    /// <summary>转换请求；默认拒绝任何无法表达的字段。</summary>
    public static LlmConversionResult ConvertRequest(JsonObject request, LlmProtocol from, LlmProtocol to, LlmConversionOptions? options = null)
    {
        Validate(request, from, to, options);
        if (from == to) return new(request, request, []);
        if (request["previous_response_id"] is not null || request.ContainsKey("store") || request.ContainsKey("background"))
            throw new LlmConversionException([new("unsupported_continuation", "$", "跨协议转换不能继承响应链或持久化执行语义。")]);
        var doc = RequestReader.Read(request, from);
        var target = RequestWriter.Write(doc, to, options ?? new());
        ConversionJson.Copy(request, "stream", target, "stream");
        Enforce(doc, options);
        return new(target, request, doc.Diagnostics);
    }

    /// <summary>转换完整单候选响应，并保留完整源 JSON。</summary>
    public static LlmConversionResult ConvertResponse(JsonObject response, LlmProtocol from, LlmProtocol to, LlmConversionOptions? options = null)
    {
        Validate(response, from, to, options);
        if (from == to) return new(response, response, []);
        var doc = ResponseMapping.Read(response, from);
        var target = ResponseMapping.Write(doc, to);
        Enforce(doc, options);
        return new(target, response, doc.Diagnostics);
    }

    /// <summary>在发起调用之前校验协议和转换选项。</summary>
    internal static void Validate(JsonObject value, LlmProtocol from, LlmProtocol to, LlmConversionOptions? options)
    {
        ArgumentNullException.ThrowIfNull(value);
        ConversionJson.ValidateProtocol(from); ConversionJson.ValidateProtocol(to);
        if (options?.DefaultMaxTokens is <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options?.TargetThinkingBudgetTokens is < 1024) throw new ArgumentOutOfRangeException(nameof(options));
        if (options is not null && !Enum.IsDefined(options.GeminiOutputFormat)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options?.DefaultSchemaName is { } schemaName
            && (schemaName.Length is < 1 or > 64 || schemaName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
            throw new ArgumentException("Schema 名称必须为 1 至 64 个 ASCII 字母、数字、下划线或连字符。", nameof(options));
        if (options?.TargetReasoningEffort is { } effort && effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("无效的目标推理强度。", nameof(options));
    }
    /// <summary>严格策略在发现损耗后拒绝输出，显式允许时完整返回诊断。</summary>
    internal static void Enforce(ConversionDocument doc, LlmConversionOptions? options)
    {
        if (doc.Diagnostics.Count != 0 && options?.AllowLoss != true) throw new LlmConversionException(doc.Diagnostics);
    }
}
