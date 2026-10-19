using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Asgard.Loki.LLM.AWS.Internal;

/// <summary>供应商私有 SigV4 HMAC-SHA256；不发现身份、校时、重签重试或缓存派生密钥。</summary>
internal static class AwsSigV4
{
    /// <summary>以实际原始正文和已选头签名；模型路径在上网编码之后按非 S3 SigV4 再编码。</summary>
    internal static void Sign(HttpRequestMessage message, byte[] body, AwsLlmSigningCredentials credentials, string region, DateTimeOffset now)
        => Sign(message, body, credentials, region, now, "bedrock");
    /// <summary>同供应商内显式选择签名服务；Mantle 不复用 Runtime 的服务作用域。</summary>
    internal static void Sign(HttpRequestMessage message, byte[] body, AwsLlmSigningCredentials credentials, string region, DateTimeOffset now, string service)
    {
        var uri = message.RequestUri!; var host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost;
        if (!uri.IsDefaultPort) host += ":" + uri.Port.ToString(CultureInfo.InvariantCulture); message.Headers.Host = host;
        message.Headers.Add("x-amz-date", now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)); message.Headers.Add("x-amz-content-sha256", Hash(body));
        if (credentials.SessionToken is { } session) message.Headers.Add("x-amz-security-token", session);
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in message.Headers) headers.Add(header.Key.ToLowerInvariant(), Normalize(string.Join(',', header.Value)));
        headers.Add("content-type", Normalize(message.Content!.Headers.ContentType!.ToString()));
        var names = string.Join(';', headers.Keys); var canonical = message.Method.Method + "\n" + CanonicalPath(uri) + "\n\n" + string.Concat(headers.Select(header => header.Key + ":" + header.Value + "\n")) + "\n" + names + "\n" + Hash(body);
        message.Headers.TryAddWithoutValidation("Authorization", Authorization(canonical, names, credentials, region, service, now));
    }
    /// <summary>标准非 S3 路径规范化与二次编码，百分号本身也编码；不把 ARN 的斜杠变成路由段。</summary>
    internal static string CanonicalPath(Uri uri) => "/" + string.Join('/', uri.AbsolutePath.Split('/').Where(segment => segment.Length != 0).Select(Uri.EscapeDataString));
    /// <summary>纯签名步骤允许以 AWS 公布的独立标准向量验证密钥派生和最终摘要。</summary>
    internal static string Authorization(string canonical, string signedHeaders, AwsLlmSigningCredentials credentials, string region, string service, DateTimeOffset now)
    {
        var date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture); var scope = date + "/" + region + "/" + service + "/aws4_request";
        var toSign = "AWS4-HMAC-SHA256\n" + now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "\n" + scope + "\n" + Hash(Encoding.UTF8.GetBytes(canonical));
        var keys = new List<byte[]> { Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey) };
        try
        {
            foreach (var value in new[] { date, region, service, "aws4_request" }) keys.Add(HMACSHA256.HashData(keys[^1], Encoding.UTF8.GetBytes(value)));
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(keys[^1], Encoding.UTF8.GetBytes(toSign)));
            return "AWS4-HMAC-SHA256 Credential=" + credentials.AccessKeyId + "/" + scope + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature;
        }
        finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
    }
    /// <summary>只规范化签名用空白；上网头和值保留已选的原始内容。</summary>
    private static string Normalize(string value) => Regex.Replace(value.Trim(), "[ \\t]+", " ", RegexOptions.CultureInvariant);
    /// <summary>完整原始字节摘要，不对正文字符串重新序列化。</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
