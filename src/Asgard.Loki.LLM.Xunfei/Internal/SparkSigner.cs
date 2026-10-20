using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Asgard.Loki.LLM.Xunfei.Internal;

/// <summary>当前星火原生 URL HMAC 鉴权，签名实际配置的主机/路径，不按 Key 或模型名选端点。</summary>
internal static class SparkSigner
{
    /// <summary>RFC1123 日期统一转 UTC，查询字段独立做 RFC3986 转义，不改变密钥字节。</summary>
    internal static Uri Sign(Uri endpoint, XunfeiSparkCredentials credentials, DateTimeOffset now)
    {
        var date = now.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
        var canonical = "host: " + endpoint.Authority + "\ndate: " + date + "\nGET " + endpoint.AbsolutePath + " HTTP/1.1";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.ApiSecret), Encoding.UTF8.GetBytes(canonical)));
        var origin = "api_key=\"" + credentials.ApiKey + "\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"" + signature + "\"";
        return new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "https" ? "wss" : "ws", Query = "authorization=" + Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(origin))) + "&date=" + Uri.EscapeDataString(date) + "&host=" + Uri.EscapeDataString(endpoint.Authority) }.Uri;
    }
}
