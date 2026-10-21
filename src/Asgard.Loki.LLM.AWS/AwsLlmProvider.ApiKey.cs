using System.Globalization;
using System.Net.Http.Headers;

namespace Asgard.Loki.LLM.AWS;

/// <summary>原生 Bedrock Runtime API Key 认证，不解析中转站复合 Key 或混用 SigV4 默认头。</summary>
public sealed partial class AwsLlmProvider
{
    /// <summary>Key 只作为不透明单个头值；实际 Host 绑定完整目标，不继承工厂默认路由。</summary>
    private static void PrepareApiKey(HttpRequestMessage message, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key); if (key.Any(character => character is <= (char)32 or > (char)126)) throw new ArgumentException("Bedrock API Key 必须是无空白/控制字符的 ASCII 头值。");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); var uri = message.RequestUri!;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        if (!uri.IsDefaultPort) host += ":" + uri.Port.ToString(CultureInfo.InvariantCulture); message.Headers.Host = host;
    }
    /// <summary>Bearer 只覆盖已选 Authorization/Host；不借用未选择的 AWS 日期、摘要、令牌或其他认证。</summary>
    private static void ValidateApiKeyDefaults(HttpClient client, HttpRequestMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var header in client.DefaultRequestHeaders)
        {
            var name = header.Key.ToLowerInvariant();
            if (name is "api-key" or "x-api-key" or "x-goog-api-key" || name.StartsWith("x-amz-", StringComparison.Ordinal)
                || name.StartsWith("x-amzn-bedrock-", StringComparison.Ordinal) && !message.Headers.Contains(name))
                throw new InvalidOperationException("Bedrock Bearer 默认头包含未选择的认证或原生请求配置。");
        }
        token.ThrowIfCancellationRequested();
    }
}
