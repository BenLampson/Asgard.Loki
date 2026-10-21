using System.Net;
using System.Text.Json;

namespace Asgard.Loki.LLM.DeepSeek;

/// <summary>完整拥有原生搜索/页面内容结果，不推断模型、计费、完成质量或自动执行后续工具。</summary>
public sealed class DeepSeekToolsResult
{
    /// <summary>仅在正文、原生结构和所有拥有的传输资源检查完成后创建。</summary>
    internal DeepSeekToolsResult(string operation, Uri endpoint, HttpStatusCode statusCode, string? contentType, JsonElement data)
    { Operation = operation; Endpoint = endpoint; StatusCode = statusCode; ContentType = contentType; Data = data.Clone(); }
    /// <summary>本次显式操作，固定为 search 或 fetch。</summary>
    public string Operation { get; }
    /// <summary>实际固定工具 API 端点，不是返回页面 URL，也不包含凭据。</summary>
    public Uri Endpoint { get; }
    /// <summary>上游实际 HTTP 状态，不按空结果或内容评分改写。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>原始响应媒体类型；缺失时保持 null。</summary>
    public string? ContentType { get; }
    /// <summary>完整原生 JSON，保留顺序、链接、分数、正文、元数据、空值和未知字段。</summary>
    public JsonElement Data { get; }
}
