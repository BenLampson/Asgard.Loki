using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Asgard.Loki.Infrastructure;

/// <summary>按标准事件边界解析借用的 SSE 流，不解析 JSON 或供应商生命周期。</summary>
public static class SseReader
{
    /// <summary>保留多行正文、事件身份和重连提示，拒绝截断事件；可选严格 UTF-8 不探测其他编码。</summary>
    public static async IAsyncEnumerable<SseFrame> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken, bool strictUtf8 = false)
    {
        using var reader = new StreamReader(stream, strictUtf8 ? new UTF8Encoding(false, true) : Encoding.UTF8,
            detectEncodingFromByteOrderMarks: !strictUtf8, 4096, leaveOpen: true);
        var data = new StringBuilder();
        string? name = null;
        string? id = null;
        long? retry = null;
        var firstLine = true;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (firstLine && strictUtf8 && line.StartsWith('\uFEFF')) line = line[1..];
            firstLine = false;
            if (line.Length == 0)
            {
                if (data.Length != 0)
                {
                    yield return new SseFrame(data.ToString(0, data.Length - 1), name, id, retry);
                    data.Clear();
                }
                name = null;
                continue;
            }
            if (line[0] == ':') continue;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "data": data.Append(value).Append('\n'); break;
                case "event": name = value; break;
                case "id" when !value.Contains('\0'): id = value; break;
                case "retry" when value.Length != 0 && value.All(char.IsAsciiDigit)
                    && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds):
                    retry = milliseconds;
                    break;
            }
        }
        if (data.Length != 0) throw new InvalidDataException("SSE 在完整事件边界之前断开。");
    }
}
