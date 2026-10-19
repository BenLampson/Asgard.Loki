namespace Asgard.Loki.LLM.Conversion;

/// <summary>显式选择 Nova Invoke 请求外壳，不从模型名或地址推断版本。</summary>
public enum BedrockNovaRequestFormat
{
    /// <summary>跨协议请求必须明确选择格式。</summary>
    Unspecified = 0,
    /// <summary>写入或要求 schemaVersion 为 messages-v1。</summary>
    MessagesV1 = 1,
    /// <summary>Nova 2 请求不写入 schemaVersion。</summary>
    Nova2 = 2
}
