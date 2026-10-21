namespace Asgard.Loki.LLM.Coze;

/// <summary>固定 Coze 原生智能体目标；底层模型由远端配置，不伪装成 SDK 模型选择或会话调度。</summary>
public sealed record CozeChatOptions
{
    /// <summary>显式地区/代理/版本根，例如 https://api.coze.cn/v3/；只追加原生 chat 路径。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>固定原生 Bot ID，不是可选择的底层 LLM 模型名。</summary>
    public required string BotId { get; init; }
    /// <summary>应用定义的完整目标身份，默认 coze-chat。</summary>
    public string TargetId { get; init; } = "coze-chat";
    /// <summary>应用定义的凭据身份；真实令牌通过客户端构造参数单独提供。</summary>
    public string CredentialId { get; init; } = "coze-token";
    /// <summary>身份相关配置版本，变化后旧引用不能静默重绑定。</summary>
    public string Version { get; init; } = "1";
    /// <summary>借用的命名 HTTP 客户端，默认 Asgard.Loki.LLM.Coze。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.LLM.Coze";
    /// <summary>原生请求 UTF-8 字节上限，默认一 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整回执/查询/消息/错误正文上限，默认三十二 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>原始 SSE 总字节上限，默认六十四 MiB，包含全部传输字段和分隔符。</summary>
    public long MaximumStreamBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>首个对话终态起保留的帧数上限，默认一百二十八。</summary>
    public int MaximumBufferedTailFrames { get; init; } = 128;
    /// <summary>流内消息关联表上限，默认四千零九十六；不按未知索引分配。</summary>
    public int MaximumTrackedMessages { get; init; } = 4096;
}
