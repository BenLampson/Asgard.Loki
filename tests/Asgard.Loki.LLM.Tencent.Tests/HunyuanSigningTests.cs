using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Tencent.Tests;

/// <summary>生产签名经真实公共调用进入替代处理器，对照独立 Python HMAC 向量。</summary>
public sealed class HunyuanSigningTests
{
    public static IEnumerable<object[]> CompleteVectors() => HunyuanFixture.Part("vectors", "tc3.json").EnumerateArray().Where(v => !v.GetProperty("stream").GetBoolean()).Select(v => new object[] { v.GetRawText() });
    /// <summary>旧/当前域名、逻辑代理 Host 和带端口物理前缀的完整签名逐字比较。</summary>
    [Theory] [MemberData(nameof(CompleteVectors))]
    public async Task ActualRequestMatchesIndependentTc3Vector(string json)
    {
        var vector = JsonSerializer.Deserialize<JsonElement>(json); using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var clock = new HunyuanClock(); var endpoint = vector.GetProperty("endpoint").GetString()!;
        var provider = new TencentHunyuanProvider(factory, source, new() { SigningHost = vector.GetProperty("signing_host").GetString() }, timeProvider: clock);
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(endpoint, message.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(vector.GetProperty("body").GetString(), await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("application/json", message.Content.Headers.ContentType!.ToString()); Assert.Equal(vector.GetProperty("host").GetString(), message.Headers.Host); Assert.Equal(vector.GetProperty("authorization").GetString(), message.Headers.GetValues("Authorization").Single());
            Assert.Equal("1792454400", message.Headers.GetValues("X-TC-Timestamp").Single()); Assert.Equal("ChatCompletions", message.Headers.GetValues("X-TC-Action").Single()); Assert.Equal("2023-09-01", message.Headers.GetValues("X-TC-Version").Single()); Assert.False(message.Headers.Contains("X-TC-Token")); Assert.False(message.Headers.Contains("X-TC-Region"));
            return HunyuanFixture.Reply(HunyuanFixture.Part("response"));
        };
        await provider.CompleteAsync(JsonNode.Parse(vector.GetProperty("body").GetString()!)!.AsObject(), HunyuanFixture.Context(endpoint), TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Calls); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(source.Disposed); Assert.False(factory.Disposed);
    }
    /// <summary>临时 Token 与区域只能由显式配置提供；原始 SecretId 不作为稳定主体。</summary>
    [Fact]
    public async Task ExplicitTemporaryTokenAndRegionAreSentWithoutRefresh()
    {
        using var source = new HunyuanSource { Credentials = new("fixture-tencent-principal", "AKIDROTATED", "rotated-fixture-secret", DateTimeOffset.MaxValue, "fixture-token") }; using var factory = new HunyuanHttpFactory();
        factory.OnSend = (message, _) => { Assert.Equal("fixture-token", message.Headers.GetValues("X-TC-Token").Single()); Assert.Equal("ap-guangzhou", message.Headers.GetValues("X-TC-Region").Single()); Assert.Contains("Credential=AKIDROTATED/", message.Headers.GetValues("Authorization").Single()); return Task.FromResult(HunyuanFixture.Reply(HunyuanFixture.Part("response"))); };
        await new TencentHunyuanProvider(factory, source, new() { Region = "ap-guangzhou" }, timeProvider: new HunyuanClock()).CompleteAsync(HunyuanFixture.Body(), HunyuanFixture.Context(), TestContext.Current.CancellationToken); Assert.Equal(1, source.Calls);
    }
    /// <summary>凭据安全默认显示/JSON 不泄漏主体、Key 或临时 Token。</summary>
    [Fact]
    public void SigningMaterialsAreAbsentFromDefaultJsonAndDisplay()
    {
        var credentials = new TencentHunyuanCredentials("private-principal", "AKIDPRIVATE", "private-secret", DateTimeOffset.MaxValue, "private-token"); var serialized = JsonSerializer.Serialize(credentials);
        Assert.Equal(nameof(TencentHunyuanCredentials), credentials.ToString()); foreach (var secret in new[] { "private-principal", "AKIDPRIVATE", "private-secret", "private-token" }) Assert.DoesNotContain(secret, serialized);
    }
    /// <summary>恶意认证材料在构造时失败，异常不回显密钥。</summary>
    [Theory] [InlineData("id", "bad/id")] [InlineData("id", "bad key")] [InlineData("key", "bad\r\nkey")] [InlineData("token", "bad token")]
    public void InvalidSigningMaterialsFailLocally(string field, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TencentHunyuanCredentials(field == "principal" ? value : "principal", field == "id" ? value : "AKIDFIXTURE", field == "key" ? value : "fixture-secret", DateTimeOffset.MaxValue, field == "token" ? value : null));
    }
    /// <summary>运行时构造孤立代理字符，避免属性元数据或测试参数传输替换原始字符。</summary>
    [Fact]
    public void InvalidOriginalPrincipalUnicodeFailsBeforeSerialization()
    {
        var invalid = new string((char)0xd800, 1);
        Assert.Throws<System.Text.EncoderFallbackException>(() => new TencentHunyuanCredentials(invalid, "AKIDFIXTURE", "fixture-secret", DateTimeOffset.MaxValue));
    }
    /// <summary>固定调用的认证方式和协议都需明确声明，不按 Key 字符串猜测。</summary>
    [Fact]
    public void ProviderDeclaresOnlyNativeProtocolAndExternalPrincipal()
    {
        using var source = new HunyuanSource(); using var factory = new HunyuanHttpFactory(); var provider = new TencentHunyuanProvider(factory, source);
        Assert.True(provider.SupportsCredential(LlmCredentialKind.External)); Assert.False(provider.SupportsCredential(LlmCredentialKind.ApiKey)); Assert.False(provider.SupportsCredential(LlmCredentialKind.Anonymous)); Assert.True(provider.SupportsProtocol(LlmProtocol.Hunyuan)); Assert.False(provider.SupportsProtocol(LlmProtocol.ChatCompletions));
    }
}
