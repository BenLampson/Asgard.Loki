namespace Asgard.Loki.LLM.Volcengine;

/// <summary>已发布 Bot 的固定原生服务配置；模型由远端应用选择，不创建模型 Session。</summary>
public sealed record VolcengineBotOptions
{
    /// <summary>显式地区/代理/版本 API 根，例如 https://ark.cn-beijing.volces.com/api/v3/bots/。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>已发布应用的原生 Bot ID，不是底层模型名称，不按前缀判断格式。</summary>
    public required string BotId { get; init; }
    /// <summary>应用定义的服务目标身份。</summary>
    public string TargetId { get; init; } = "ark-bot";
    /// <summary>应用定义的凭据身份；真实 API Key 独立传入客户端。</summary>
    public string CredentialId { get; init; } = "ark-bot-key";
    /// <summary>调用者的固定配置版本。</summary>
    public string Version { get; init; } = "1";
    /// <summary>借用的命名 HTTP 工厂客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.LLM.Volcengine.Bot";
    /// <summary>原生 HTTP 字节、终态尾部和关联策略，在构造时完整快照。</summary>
    public VolcengineHttpOptions HttpOptions { get; init; } = new();
}
