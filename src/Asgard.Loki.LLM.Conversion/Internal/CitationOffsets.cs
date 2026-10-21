using System.Buffers;
using System.Text;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>按明确单位转换完整 Unicode 标量边界，不归一化文本或推断字形长度。</summary>
internal static class CitationOffsets
{
    /// <summary>验证范围并返回目标单位偏移；不接受半个代理对或半个 UTF-8 编码。</summary>
    internal static (long Start, long End) Convert(string text, long start, long end, LlmCitationIndexUnit source, LlmCitationIndexUnit target)
    {
        if (source == LlmCitationIndexUnit.Unspecified || target == LlmCitationIndexUnit.Unspecified || start < 0 || end < start)
            throw new InvalidDataException("引用范围或索引单位无效。");
        long from = 0, to = 0, mappedStart = -1, mappedEnd = -1; var position = 0;
        while (true)
        {
            if (from == start) mappedStart = to;
            if (from == end) mappedEnd = to;
            if (position == text.Length) break;
            if (Rune.DecodeFromUtf16(text.AsSpan(position), out var rune, out var consumed) != OperationStatus.Done)
                throw new InvalidDataException("引用文本含无效 UTF-16。");
            from = checked(from + Width(rune, source)); to = checked(to + Width(rune, target)); position += consumed;
        }
        if (mappedStart < 0 || mappedEnd < 0) throw new InvalidDataException("引用范围超出文本或切断 Unicode 标量。");
        return (mappedStart, mappedEnd);
    }

    /// <summary>计算完整文本在声明单位下的长度，同时验证 Unicode。</summary>
    internal static long Length(string text, LlmCitationIndexUnit unit)
    {
        long length = 0; var position = 0;
        while (position < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(position), out var rune, out var consumed) != OperationStatus.Done)
                throw new InvalidDataException("引用文本含无效 UTF-16。");
            length = checked(length + Width(rune, unit)); position += consumed;
        }
        return length;
    }

    /// <summary>标量、代码单元与字节各自计数，不能混用。</summary>
    private static int Width(Rune rune, LlmCitationIndexUnit unit) => unit switch
    {
        LlmCitationIndexUnit.UnicodeScalar => 1,
        LlmCitationIndexUnit.Utf16CodeUnit => rune.Utf16SequenceLength,
        LlmCitationIndexUnit.Utf8Byte => rune.Utf8SequenceLength,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };
}
