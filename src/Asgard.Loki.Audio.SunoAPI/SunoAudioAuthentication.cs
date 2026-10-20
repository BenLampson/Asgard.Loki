namespace Asgard.Loki.Audio.SunoAPI;

/// <summary>应用明确选择代理的认证方式，不读取 Suno 账号或浏览器状态。</summary>
public enum SunoAudioAuthentication
{
    /// <summary>使用应用配置的代理 Bearer 凭据。</summary>
    Bearer,
    /// <summary>应用明确配置的匿名代理，不发送凭据。</summary>
    Anonymous
}
