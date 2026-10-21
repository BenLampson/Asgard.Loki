namespace Asgard.Loki.LLM;

/// <summary>适配器可选的协议范围声明；仍必须提供对应接口，不代替操作/字段支持矩阵。</summary>
public interface ILlmProtocolSupport
{
    /// <summary>仅判断一个明确原生/公开协议；不得联网探测模型、改配置或选择另一上游。</summary>
    bool SupportsProtocol(LlmProtocol protocol);
}
