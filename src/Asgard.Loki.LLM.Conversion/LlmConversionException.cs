namespace Asgard.Loki.LLM.Conversion;

/// <summary>表示转换将损失语义，包含字段路径而不包含用户正文或密钥。</summary>
public sealed class LlmConversionException : NotSupportedException
{
    /// <summary>复制诊断并拒绝无法保真的转换。</summary>
    public LlmConversionException(IEnumerable<LlmConversionDiagnostic> diagnostics) : base("协议转换无法等价表达输入；请检查 Diagnostics。")
        => Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    /// <summary>导致拒绝的转换诊断。</summary>
    public IReadOnlyList<LlmConversionDiagnostic> Diagnostics { get; }
}
