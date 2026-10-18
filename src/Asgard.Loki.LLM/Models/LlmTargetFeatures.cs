namespace Asgard.Loki.LLM;

/// <summary>目标显式开启的原生可选操作；协议支持不等于支持每种扩展操作。</summary>
[Flags]
public enum LlmTargetFeatures
{
    /// <summary>只使用目标声明的基础协议。</summary>
    None = 0,
    /// <summary>原生 Responses 压缩窗口操作，不自动压缩或转换协议。</summary>
    ResponsesCompaction = 1,
    /// <summary>在完整固定目标上使用原生 Responses 双工连接。</summary>
    ResponsesWebSocket = 2
}
