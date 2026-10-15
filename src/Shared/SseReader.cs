using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Asgard.Loki.Internal;

/// <summary>内部按事件边界解析 SSE，供各供应商独立编译使用。</summary>
internal static class SseReader
{
    /// <summary>保留多行正文、事件身份和重连提示，拒绝截断事件。</summary>
    internal static async IAsyncEnumerable<SseFrame> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var data = new StringBuilder();
        string? name = null;
        string? id = null;
        long? retry = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
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
