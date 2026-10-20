namespace Asgard.Loki.Video;

/// <summary>原生 multipart 文本字段，保留重复名称、原始文本和顺序，不自动展开对象或数组。</summary>
public sealed record VideoMultipartField(string Name, string Value);
