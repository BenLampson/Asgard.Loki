namespace Asgard.Loki.LLM.Conversion;

/// <summary>调用者对固定 OpenAI 端点 URL 引用索引单位的明确声明。</summary>
public enum LlmCitationIndexUnit
{
    /// <summary>单位未知；只复制未变文本范围或平移 ASCII 前缀，不解释 Gemini 字节范围。</summary>
    Unspecified,
    /// <summary>以 Unicode 标量值计数；组合字符不折叠为字形。</summary>
    UnicodeScalar,
    /// <summary>以 UTF-16 代码单元计数，范围仍须位于完整 Unicode 标量边界。</summary>
    Utf16CodeUnit,
    /// <summary>以 UTF-8 字节计数，范围须位于完整 Unicode 标量边界。</summary>
    Utf8Byte
}
