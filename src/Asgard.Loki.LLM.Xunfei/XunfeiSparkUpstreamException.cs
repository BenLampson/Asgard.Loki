using System.Text.Json;

namespace Asgard.Loki.LLM.Xunfei;

/// <summary>星火 WebSocket 原生非零错误；保留原始消息，不补造 HTTP 状态或生成终态。</summary>
public sealed class XunfeiSparkUpstreamException : Exception
{
    /// <summary>复制完整原生错误字节与 JSON 所有权，错误正文不进入默认异常文本。</summary>
    public XunfeiSparkUpstreamException(int code, ReadOnlyMemory<byte> body, JsonElement nativeData) : base("讯飞星火返回原生错误。")
    { Code = code; Body = body.ToArray(); NativeData = nativeData.Clone(); }
    /// <summary>原始 header.code，不转换成 HTTP 状态。</summary>
    public int Code { get; }
    /// <summary>未经改写的完整原生错误消息 UTF-8 字节。</summary>
    public ReadOnlyMemory<byte> Body { get; }
    /// <summary>完整原生错误对象，包括 sid、message 及未知字段。</summary>
    public JsonElement NativeData { get; }
}
