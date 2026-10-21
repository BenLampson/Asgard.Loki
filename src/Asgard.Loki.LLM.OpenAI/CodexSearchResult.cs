using System.Net;

namespace Asgard.Loki.LLM.OpenAI;

/// <summary>完整原生搜索 HTTP 窗口，不猜测返回 JSON 结构、提取答案或推导用量。</summary>
public sealed class CodexSearchResult
{
    private readonly byte[] _body;
    /// <summary>资源关闭和最终绑定/取消检查之后取得独立响应字节。</summary>
    internal CodexSearchResult(Uri endpoint, HttpStatusCode status, string? contentType, ReadOnlySpan<byte> body, CodexSearchMetadata metadata)
    { Endpoint = endpoint; StatusCode = status; ContentType = contentType; _body = body.ToArray(); Metadata = metadata; }
    /// <summary>本次固定服务实际 alpha/search 地址，不是返回资料中的外部 URL。</summary>
    public Uri Endpoint { get; }
    /// <summary>实际 HTTP 状态，不等同于模型完成或资料有效性。</summary>
    public HttpStatusCode StatusCode { get; }
    /// <summary>完整原始媒体头，缺失时保持 null。</summary>
    public string? ContentType { get; }
    /// <summary>原始响应字节的独立副本，允许未知 JSON、文本、二进制或空正文；不自动解码/访问引用。</summary>
    public ReadOnlyMemory<byte> Body => _body.ToArray();
    /// <summary>固定服务/账户/凭据身份，不包含令牌或虚构实际模型。</summary>
    public CodexSearchMetadata Metadata { get; }
}
