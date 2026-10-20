using System.Collections.ObjectModel;
using System.Net;

namespace Asgard.Loki.Video;

/// <summary>显式内容复制完成后的原生响应元数据，不代表新的生成任务完成事件。</summary>
public sealed class VideoContentResult
{
    /// <summary>保存原始任务关联、字节计数、HTTP 状态和完整独立多值头部快照。</summary>
    public VideoContentResult(string taskId, long bytesWritten, HttpStatusCode statusCode, IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId); ArgumentOutOfRangeException.ThrowIfNegative(bytesWritten); ArgumentNullException.ThrowIfNull(headers);
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in headers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Key); ArgumentNullException.ThrowIfNull(field.Value);
            var values = field.Value.ToArray(); foreach (var value in values) ArgumentNullException.ThrowIfNull(value);
            copy.Add(field.Key, Array.AsReadOnly(values));
        }
        TaskId = taskId; BytesWritten = bytesWritten; StatusCode = statusCode; Headers = new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
    }
    /// <summary>本次明确读取的原始任务 ID。</summary>
    public string TaskId { get; }
    /// <summary>已成功写入借用目标流的完整原生字节数。</summary>
    public long BytesWritten { get; }
    /// <summary>原始 HTTP 状态，不折叠为生成状态。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>完整响应及内容头部的独立只读多值快照，名称忽略大小写查找。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }
}
