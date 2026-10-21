namespace Asgard.Loki.LLM.Dify;

/// <summary>固定应用服务目标；底层模型由应用的远端配置选择。</summary>
public sealed record DifyChatOptions
{
    /// <summary>显式地区/自托管/代理/版本 API 根，只追加 chat-messages。</summary>
    public required Uri BaseAddress { get; init; }
    /// <summary>显式应用类别，不能由响应或密钥推断。</summary>
    public required DifyChatProfile Profile { get; init; }
    /// <summary>应用定义的目标身份，默认 dify-chat。</summary>
    public string TargetId { get; init; } = "dify-chat";
    /// <summary>应用定义的凭据身份，真实 Key 独立传入构造函数。</summary>
    public string CredentialId { get; init; } = "dify-app-key";
    /// <summary>固定配置版本，默认一。</summary>
    public string Version { get; init; } = "1";
    /// <summary>借用的命名 HTTP 工厂客户端名称。</summary>
    public string HttpClientName { get; init; } = "Asgard.Loki.LLM.Dify";
    /// <summary>完整原生请求 UTF-8 字节上限，默认一 MiB。</summary>
    public int MaximumRequestBytes { get; init; } = 1024 * 1024;
    /// <summary>完整 JSON/错误响应字节上限，默认三十二 MiB。</summary>
    public int MaximumResponseBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>完整 SSE 原始字节上限，默认六十四 MiB，含心跳和分隔符。</summary>
    public long MaximumStreamBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>首个生成结束阶段起保留的尾部帧数上限，默认一百二十八。</summary>
    public int MaximumBufferedTailFrames { get; init; } = 128;
}
