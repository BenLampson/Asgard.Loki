namespace Asgard.Loki.LLM;

/// <summary>描述显式跨协议转换中无法等价表达的字段。</summary>
public sealed record LlmConversionDiagnostic(string Code, string Path, string Message);
