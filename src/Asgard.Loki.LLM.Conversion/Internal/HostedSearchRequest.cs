using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>一次请求内的私有搜索意图，不是公开统一业务模型，也不持有执行器。</summary>
internal sealed class HostedSearchRequest(LlmProtocol source, string path, JsonObject settings)
{
    /// <summary>原始协议，用于区分原生选择器和字段路径。</summary>
    internal LlmProtocol Source { get; } = source;
    /// <summary>原始工具位置，诊断不回显查询、域名或用户位置。</summary>
    internal string Path { get; } = path;
    /// <summary>完整保留出现/省略差异的已校验私有设置。</summary>
    internal JsonObject Settings { get; } = settings;
    /// <summary>显式全局或搜索选择；function 表示普通函数选择由既有转换层处理。</summary>
    internal string? Choice { get; set; }
}
