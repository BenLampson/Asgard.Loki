namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>原生响应的有限关联状态，不积累输出内容或执行工具。</summary>
internal sealed class ResponsesWebSocketResponseState(string lane)
{
    /// <summary>响应原生 lane。</summary>
    internal string Lane { get; } = lane;
    /// <summary>是否已经接收终态。</summary>
    internal bool Terminal { get; set; }
    /// <summary>是否接收过 created。</summary>
    internal bool Created { get; set; }
    /// <summary>最后一个真实上游序号。</summary>
    internal long? Sequence { get; set; }
    /// <summary>曾发送不能由生成终态证明已确认的控制；迟到无 ID 错误仍可能属于它。</summary>
    internal bool UnconfirmedControlHistory { get; set; }
}
