namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>仅保存关联所需的提交身份，不缓存输入、工具结果或历史正文。</summary>
internal sealed class ResponsesWebSocketSubmission(string type, string lane, string? previous, string? eventId)
{
    /// <summary>客户端原生事件类型。</summary>
    internal string Type { get; } = type;
    /// <summary>消息所属 lane。</summary>
    internal string Lane { get; } = lane;
    /// <summary>原生引用响应。</summary>
    internal string? Previous { get; } = previous;
    /// <summary>显式控制关联事件 ID。</summary>
    internal string? EventId { get; } = eventId;
    /// <summary>仅服务器接受后提供的 steering ID。</summary>
    internal string? SteerId { get; set; }
    /// <summary>本提交首次设置的响应控制历史，仅用于未发送时回滚。</summary>
    internal ResponsesWebSocketResponseState? AddedControlHistory { get; set; }
    /// <summary>本提交首次设置了无法定位 lane 的控制历史。</summary>
    internal bool AddedUnscopedHistory { get; set; }
}
