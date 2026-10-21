namespace Asgard.Loki.LLM.Coze;

/// <summary>原生服务目标元数据；Bot ID 与实际底层模型明确区分，不包含令牌。</summary>
public sealed record CozeChatMetadata(string TargetId, Uri BaseAddress, string BotId, string CredentialId, string Version);
