namespace Asgard.Loki.LLM.Volcengine.IntegrationTests;

/// <summary>只读取约定的四个变量；地址与请求约束由测试代码定义。</summary>
internal sealed class LiveConfiguration
{
    /// <summary>标准方舟数据面地址，调用者仍可通过目标配置覆盖。</summary>
    internal static readonly Uri DefaultBaseAddress = new("https://ark.cn-beijing.volces.com/api/v3/");
    /// <summary>本次联调最多发送五次生成请求，不自动重试。</summary>
    internal const int MaximumRequests = 5;
    /// <summary>单次请求包含流枚举的完整截止时间。</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    /// <summary>仅保存在测试进程内，不输出或序列化。</summary>
    internal required string ApiKey { get; init; }
    /// <summary>Chat 使用的实际模型。</summary>
    internal required string ChatModel { get; init; }
    /// <summary>Responses 使用的实际模型。</summary>
    internal required string ResponsesModel { get; init; }

    /// <summary>未配置或显式关闭时跳过；无效开关属于配置错误。</summary>
    internal static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable("ASGARD_LOKI_TEST_LIVE");
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (bool.TryParse(value, out var enabled)) return enabled;
        throw new InvalidOperationException("ASGARD_LOKI_TEST_LIVE must be true or false.");
    }

    /// <summary>在创建网络客户端之前验证完整场景配置。</summary>
    internal static LiveConfiguration Load()
    {
        const string prefix = "ASGARD_LOKI_TEST_VOLCENGINE_LLM_";
        var names = new[] { prefix + "API_KEY", prefix + "CHAT_MODEL", prefix + "RESPONSES_MODEL" };
        var values = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var missing = names.Where((_, index) => string.IsNullOrWhiteSpace(values[index])).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException("Missing variables: " + string.Join(", ", missing));
        return new LiveConfiguration { ApiKey = values[0]!, ChatModel = values[1]!, ResponsesModel = values[2]! };
    }
}
