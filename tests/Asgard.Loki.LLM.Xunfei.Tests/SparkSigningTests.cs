using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.Xunfei.Tests;

/// <summary>完整公开调用对照独立 Python HMAC 向量，不调用生产签名器生成预期值。</summary>
public sealed class SparkSigningTests
{
    public static IEnumerable<object[]> Vectors() => SparkFixture.Part("vectors", "signatures.json").EnumerateArray().Select(v => new object[] { v.GetRawText() });
    /// <summary>UTC/带时区时间和显式域名/路径/端口的八个握手 URL 逐字相符。</summary>
    [Theory] [MemberData(nameof(Vectors))]
    public async Task ActualHandshakeMatchesIndependentSignature(string json)
    {
        var vector = JsonSerializer.Deserialize<JsonElement>(json); using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var clock = new SparkClock { Now = DateTimeOffset.Parse(vector.GetProperty("instant").GetString()!, CultureInfo.InvariantCulture) }; var domain = vector.GetProperty("domain").GetString()!; var endpoint = vector.GetProperty("base_address").GetString()!; var body = SparkFixture.Body(); body["parameter"]!["chat"]!["domain"] = domain;
        Assert.Equal(4, (await SparkFixture.ReadAsync(new(source, SparkFixture.Policy(), factory, timeProvider: clock), TestContext.Current.CancellationToken, body, SparkFixture.Context(endpoint, domain))).Count);
        Assert.Equal(vector.GetProperty("signed_uri").GetString(), factory.Address!.AbsoluteUri); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls); Assert.Equal(domain, JsonSerializer.Deserialize<JsonElement>(factory.Socket.Sent!).GetProperty("parameter").GetProperty("chat").GetProperty("domain").GetString());
    }
    /// <summary>逻辑主体、Key/Secret 不进入默认 JSON、显示或供应商绑定身份。</summary>
    [Fact]
    public void SigningMaterialsStayOutOfDefaultSerializationAndIdentity()
    {
        using var source = new SparkSource(); using var factory = new SparkSocketFactory(); var credentials = source.Credentials; Assert.Equal(nameof(XunfeiSparkCredentials), credentials.ToString()); var serialized = JsonSerializer.Serialize(credentials); var identity = new XunfeiSparkProvider(source, SparkFixture.Policy(), factory).BindingIdentity;
        foreach (var secret in new[] { credentials.ApiKey, credentials.ApiSecret, credentials.PrincipalIdentity }) { Assert.DoesNotContain(secret, serialized); Assert.DoesNotContain(secret, identity); }
    }
    /// <summary>非法认证字符串在本地构造阶段失败，原始代理字符必须运行时构造。</summary>
    [Theory] [InlineData("key-empty")] [InlineData("secret-empty")] [InlineData("key-control")] [InlineData("secret-control")] [InlineData("quote")] [InlineData("escape")] [InlineData("unicode")]
    public void InvalidCredentialsFailWithoutTransport(string kind)
    {
        Assert.ThrowsAny<ArgumentException>(() => new XunfeiSparkCredentials(kind == "unicode" ? new string((char)0xd800, 1) : "principal", kind == "key-empty" ? "" : kind == "key-control" ? "key\r\n" : kind == "quote" ? "bad\"key" : kind == "escape" ? "bad\\key" : "fixture-key", kind == "secret-empty" ? "" : kind == "secret-control" ? "bad secret" : "fixture-secret", DateTimeOffset.MaxValue));
    }
    /// <summary>显式原生来源提供含竖线的 Key 仍整体签名，不解析 AppId/Secret 或触发协议分流。</summary>
    [Fact]
    public async Task NativeKeyIsOpaqueEvenWhenItContainsPipes()
    {
        using var source = new SparkSource { Credentials = new("fixture-spark-principal", "opaque|native|key", "fixture-secret", DateTimeOffset.MaxValue) }; using var factory = new SparkSocketFactory(); await SparkFixture.ReadAsync(new(source, SparkFixture.Policy(), factory, timeProvider: new SparkClock()), TestContext.Current.CancellationToken);
        var query = factory.Address!.Query.TrimStart('?').Split('&').ToDictionary(x => x.Split('=')[0], x => Uri.UnescapeDataString(x[(x.IndexOf('=') + 1)..])); var origin = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(query["authorization"])); Assert.StartsWith("api_key=\"opaque|native|key\",", origin); Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls);
    }
}
