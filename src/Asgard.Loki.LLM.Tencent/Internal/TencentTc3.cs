using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Asgard.Loki.LLM.Tencent.Internal;

/// <summary>混元 API 3.0 原生 TC3，只签名实际发送字节；规范路径固定为 /。</summary>
internal static class TencentTc3
{
    /// <summary>主机、媒体类型和 Action 与最终请求头一致；区域不进入凭据作用域。</summary>
    internal static void Sign(HttpRequestMessage message, byte[] body, string host, TencentHunyuanCredentials credentials, DateTimeOffset now, string? region)
    {
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture); var date = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var canonical = "POST\n/\n\ncontent-type:application/json\nhost:" + host.ToLowerInvariant() + "\nx-tc-action:chatcompletions\n\ncontent-type;host;x-tc-action\n" + Hash(body);
        var scope = date + "/hunyuan/tc3_request"; var text = "TC3-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + Hash(Encoding.UTF8.GetBytes(canonical));
        var dateKey = Hmac(Encoding.UTF8.GetBytes("TC3" + credentials.SecretKey), date); var serviceKey = Hmac(dateKey, "hunyuan"); var signingKey = Hmac(serviceKey, "tc3_request");
        try
        {
            message.Headers.TryAddWithoutValidation("Authorization", "TC3-HMAC-SHA256 Credential=" + credentials.SecretId + "/" + scope + ", SignedHeaders=content-type;host;x-tc-action, Signature=" + Convert.ToHexStringLower(Hmac(signingKey, text)));
            message.Headers.Host = host; message.Headers.Add("X-TC-Action", "ChatCompletions"); message.Headers.Add("X-TC-Version", "2023-09-01"); message.Headers.Add("X-TC-Timestamp", timestamp);
            if (region is not null) message.Headers.Add("X-TC-Region", region);
            if (credentials.Token is not null) message.Headers.Add("X-TC-Token", credentials.Token);
        }
        finally { CryptographicOperations.ZeroMemory(dateKey); CryptographicOperations.ZeroMemory(serviceKey); CryptographicOperations.ZeroMemory(signingKey); }
    }
    /// <summary>原生 TC3 使用小写十六进制 SHA-256。</summary>
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    /// <summary>逐层派生签名 Key，不改变原始密钥内容。</summary>
    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}
