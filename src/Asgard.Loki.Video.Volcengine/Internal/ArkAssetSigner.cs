using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Asgard.Loki.Video.Volcengine.Internal;

/// <summary>仅为固定方舟素材 JSON 操作签名，不复用其他供应商认证或执行凭据发现。</summary>
internal static class ArkAssetSigner
{
    /// <summary>签名请求实际正文、根路径、固定查询及完整认证头，支持显式临时凭据。</summary>
    internal static void Sign(HttpRequestMessage request, byte[] body, string accessKey, string secret, string? token, string region, DateTimeOffset now)
    {
        var uri = request.RequestUri!;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        if (!uri.IsDefaultPort) host += ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        request.Headers.Host = host;
        var date = now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var hash = Hex(SHA256.HashData(body));
        request.Headers.Add("X-Date", date); request.Headers.Add("X-Content-Sha256", hash);
        if (token is not null) request.Headers.Add("X-Security-Token", token);
        var headers = "content-type:application/json\nhost:" + host + "\nx-content-sha256:" + hash + "\nx-date:" + date + "\n";
        var names = "content-type;host;x-content-sha256;x-date";
        if (token is not null) { headers += "x-security-token:" + token + "\n"; names += ";x-security-token"; }
        var canonical = "POST\n/\n" + uri.Query[1..] + "\n" + headers + "\n" + names + "\n" + hash;
        var scope = date[..8] + "/" + region + "/ark/request";
        var toSign = "HMAC-SHA256\n" + date + "\n" + scope + "\n" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var key = Encoding.UTF8.GetBytes(secret);
        try
        {
            foreach (var component in new[] { date[..8], region, "ark", "request" })
            {
                var next = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(component)); CryptographicOperations.ZeroMemory(key); key = next;
            }
            var signature = Hex(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(toSign)));
            // 方舟的 SignedHeaders 使用未加引号的分号列表，不能交给通用认证参数解析器重新解释。
            request.Headers.TryAddWithoutValidation("Authorization", "HMAC-SHA256 Credential=" + accessKey + "/" + scope + ", SignedHeaders=" + names + ", Signature=" + signature);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    /// <summary>签名协议使用小写十六进制，不进行 Base64 或 URL 二次编码。</summary>
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
