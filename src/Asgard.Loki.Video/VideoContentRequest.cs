using Asgard.Loki.Video.Internal;

namespace Asgard.Loki.Video;

/// <summary>一次显式原生任务内容读取的查询字段，不合成下载 URL 或派生媒体格式。</summary>
public sealed record VideoContentRequest
{
    /// <summary>原生查询字段，保留重复名称、顺序、空文本和不透明值，由供应商进行 URL 编码。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Query { get; init; } = [];
    /// <summary>在等待前复制查询元数据，不修改字段名称或值。</summary>
    public VideoContentRequest Snapshot()
    {
        ArgumentNullException.ThrowIfNull(Query); var query = Query.ToArray();
        foreach (var field in query)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Key); ArgumentNullException.ThrowIfNull(field.Value);
            VideoJson.Text(field.Key); VideoJson.Text(field.Value);
        }
        return this with { Query = Array.AsReadOnly(query) };
    }
}
