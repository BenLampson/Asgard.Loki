using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Asgard.Loki.Image.Volcengine.Internal;

/// <summary>Visual 图片固定根路径 POST 的原生签名，不引用视频包或维护任务状态。</summary>
internal static class JimengImageSigner
{
    /// <summary>以实际请求字节、固定查询及明确的临时令牌签名，时钟由调用者借用。</summary>
    internal static void Sign(HttpRequestMessage request, byte[] body, JimengImageOptions options, DateTimeOffset now)
    {
        var uri = request.RequestUri!;
        var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        if (!uri.IsDefaultPort) host += ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        request.Headers.Host = host;
        var date = now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var hash = Convert.ToHexStringLower(SHA256.HashData(body));
        request.Headers.Add("X-Date", date); request.Headers.Add("X-Content-Sha256", hash);
        var headers = "content-type:application/json\nhost:" + host + "\nx-content-sha256:" + hash + "\nx-date:" + date + "\n";
        var names = "content-type;host;x-content-sha256;x-date";
        if (options.SecurityToken is { } token)
        { request.Headers.Add("X-Security-Token", token); headers += "x-security-token:" + token + "\n"; names += ";x-security-token"; }
        var canonical = "POST\n/\n" + uri.Query[1..] + "\n" + headers + "\n" + names + "\n" + hash;
        var scope = date[..8] + "/cn-north-1/cv/request";
        var toSign = "HMAC-SHA256\n" + date + "\n" + scope + "\n" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var key = Encoding.UTF8.GetBytes(options.SecretAccessKey);
        try
        {
            foreach (var component in scope.Split('/'))
            { var next = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(component)); CryptographicOperations.ZeroMemory(key); key = next; }
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(toSign)));
            request.Headers.TryAddWithoutValidation("Authorization", "HMAC-SHA256 Credential=" + options.AccessKeyId + "/" + scope + ", SignedHeaders=" + names + ", Signature=" + signature);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
