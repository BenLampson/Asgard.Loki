namespace Asgard.Loki.LLM.Conversion;

/// <summary>由调用者声明内联纯文本文件的编码，不探测字节、文件名或模型。</summary>
public enum LlmTextFileEncoding
{
    /// <summary>没有编码声明，不进行纯文本文件转换。</summary>
    Unspecified,
    /// <summary>严格 UTF-8；非法字节和未配对代理项均失败，不替换、规范化或去除 BOM。</summary>
    Utf8
}
