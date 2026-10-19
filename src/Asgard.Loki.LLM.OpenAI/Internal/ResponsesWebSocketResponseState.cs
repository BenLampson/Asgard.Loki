namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>原生响应的有限关联状态，不积累输出内容或执行工具。</summary>
internal sealed class ResponsesWebSocketResponseState(string lane, string? eventId)
{
    /// <summary>响应原生 lane。</summary>
    internal string Lane { get; } = lane;
    /// <summary>触发该响应的显式事件 ID；用于区分已开始生成与后来排队提交的错误。</summary>
    internal string? EventId { get; } = eventId;
    /// <summary>是否已经接收终态。</summary>
    internal bool Terminal { get; set; }
    /// <summary>是否接收过 created。</summary>
    internal bool Created { get; set; }
    /// <summary>最后一个真实上游序号。</summary>
    internal long? Sequence { get; set; }
}
