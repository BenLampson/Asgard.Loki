namespace Asgard.Loki.Audio;

/// <summary>原生 multipart 文本字段；名称和值为原文，重复名称、空值与顺序均有意义。</summary>
public sealed record AudioMultipartField(string Name, string Value);
