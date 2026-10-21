using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Video.Kling.Internal;

/// <summary>仅为可灵旧版接口产生原生短期 JWT，不缓存、验证外部令牌或承担登录服务。</summary>
internal static class KlingVideoJwt
{
    /// <summary>固定 HS256/iss/exp/nbf 契约，30 分钟有效期及五秒生效偏移。</summary>
    internal static (string Token, long NotBefore, long ExpiresOn) Create(string accessKey, string secret, long unixTime)
    {
        var expires = checked(unixTime + 1800); var notBefore = checked(unixTime - 5);
        var header = Encode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8);
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = accessKey, exp = expires, nbf = notBefore }));
        var unsigned = header + "." + payload; var key = Encoding.UTF8.GetBytes(secret);
        try { return (unsigned + "." + Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(unsigned))), notBefore, expires); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    /// <summary>原生 JWT 使用无填充的 base64url，不采用普通 Base64 或转义后的字符串。</summary>
    private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
