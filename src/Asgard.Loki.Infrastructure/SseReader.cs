using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Asgard.Loki.Infrastructure;

/// <summary>按标准事件边界解析借用的 SSE 流，不解析 JSON 或供应商生命周期。</summary>
public static class SseReader
{
    /// <summary>以显式原始字节预算读取 SSE，验证完整流长度；不解释供应商终态或释放借用输入。</summary>
    /// <param name="stream">借用输入流，不访问 Length/Position 或自动关闭。</param>
    /// <param name="maximumBytes">含 BOM、注释、控制字段与分隔符的全部原始字节上限，必须为正。</param>
    /// <param name="cancellationToken">贯穿读取、帧交付和最终 EOF 的取消令牌。</param>
    /// <param name="strictUtf8">是否拒绝非法 UTF-8；默认保持原有宽松解码契约。</param>
    /// <param name="expectedLength">可选原始长度声明，必须非负且不超过预算，并在 EOF 精确匹配。</param>
    /// <returns>完整原始 SSE 帧；普通枚举完成不能代替供应商成功、终态及外层资源释放检查。</returns>
    public static async IAsyncEnumerable<SseFrame> ReadBoundedAsync(Stream stream, long maximumBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, bool strictUtf8 = false, long? expectedLength = null)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var bounded = new BoundedReadStream(stream, maximumBytes, expectedLength);
        await foreach (var frame in ReadAsync(bounded, cancellationToken, strictUtf8).ConfigureAwait(false))
        { cancellationToken.ThrowIfCancellationRequested(); yield return frame; }
        cancellationToken.ThrowIfCancellationRequested();
    }
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
