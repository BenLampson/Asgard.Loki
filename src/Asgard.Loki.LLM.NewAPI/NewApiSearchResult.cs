using System.Net;

namespace Asgard.Loki.LLM.NewAPI;

/// <summary>完整原生搜索 HTTP 窗口，不解释正文结构、提取答案或推导模型终态。</summary>
public sealed class NewApiSearchResult
{
    private readonly byte[] _body;
    /// <summary>仅在真实 EOF、自有资源关闭与最终取消检查之后建立独立正文。</summary>
    internal NewApiSearchResult(Uri endpoint, HttpStatusCode status, string? contentType, ReadOnlySpan<byte> body, NewApiSearchMetadata metadata)
    { Endpoint = endpoint; StatusCode = status; ContentType = contentType; _body = body.ToArray(); Metadata = metadata; }
    /// <summary>本次固定目标的实际 alpha/search 地址，不是正文中的外部资料 URL。</summary>
    public Uri Endpoint { get; }
    /// <summary>实际 HTTP 状态，不等同于模型生成完成或资料有效。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>完整原始媒体类型字段，缺失时保持 null。</summary>
    public string? ContentType { get; }
    /// <summary>原始 JSON、文本、二进制、SSE 或空正文的独立副本，不解码或访问引用。</summary>
    public ReadOnlyMemory<byte> Body => _body.ToArray();
    /// <summary>固定完整服务、模型与凭据引用。</summary>
    public NewApiSearchMetadata Metadata { get; }
}
