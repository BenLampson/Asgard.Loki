namespace Asgard.Loki.LLM;

/// <summary>缓存中的完整路由身份，不包含认证密钥。</summary>
public sealed record LlmSessionBinding(string SessionId, string Model, LlmProtocol Protocol, string TargetId, string Fingerprint);
