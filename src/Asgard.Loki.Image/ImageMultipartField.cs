namespace Asgard.Loki.Image;

/// <summary>供应商原生 multipart 文本字段；重复名称、原始文本、空值和顺序均保持不变。</summary>
public sealed record ImageMultipartField(string Name, string Value);
