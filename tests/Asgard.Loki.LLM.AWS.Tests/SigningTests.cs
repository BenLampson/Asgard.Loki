using System.Text;
using System.Text.Json;
using Asgard.Loki.LLM.AWS.Internal;
using Xunit;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>AWS 公布向量与独立路径样本验证规范化和密钥派生，不调用网络。</summary>
public sealed class SigningTests
{
    /// <summary>AWS IAM 公开 ListUsers 示例验证最终标准签名，示例 Key 不是实际凭据。</summary>
    [Fact]
    public void PublishedAwsIamVector_MatchesExactAuthorization()
    {
        const string canonical = "GET\n/\nAction=ListUsers&Version=2010-05-08\ncontent-type:application/x-www-form-urlencoded; charset=utf-8\nhost:iam.amazonaws.com\nx-amz-date:20150830T123600Z\n\ncontent-type;host;x-amz-date\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; var clock = new AwsClock(); var credentials = new AwsLlmSigningCredentials("principal", AwsCredentialSource.KeyId, AwsCredentialSource.Secret, clock.Now.AddHours(1)); var actual = AwsSigV4.Authorization(canonical, "content-type;host;x-amz-date", credentials, "us-east-1", "iam", clock.Now); Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7", actual);
    }
    /// <summary>非 S3 二次编码和路径规范化不会把编码模型分隔符变成新路由段。</summary>
    [Theory] [InlineData("https://host.invalid/model/a%3Ab%2Fc/invoke", "/model/a%253Ab%252Fc/invoke")] [InlineData("https://host.invalid/proxy//model/m/invoke", "/proxy/model/m/invoke")] [InlineData("https://host.invalid/model/a%252Fb/invoke", "/model/a%25252Fb/invoke")]
    public void CanonicalPath_UsesNonS3DoubleEncoding(string uri, string expected) => Assert.Equal(expected, AwsSigV4.CanonicalPath(new(uri)));
    /// <summary>凭据自身默认显示/JSON 不泄漏主体、Key、Secret 或临时 Token。</summary>
    [Fact]
    public void Credential_DefaultRepresentationsContainNoSecrets()
    {
        var credentials = new AwsLlmSigningCredentials("private-principal", AwsCredentialSource.KeyId, AwsCredentialSource.Secret, new AwsClock().Now, "private-token"); var display = credentials.ToString() + JsonSerializer.Serialize(credentials); Assert.DoesNotContain("private-principal", display); Assert.DoesNotContain(AwsCredentialSource.KeyId, display); Assert.DoesNotContain(AwsCredentialSource.Secret, display); Assert.DoesNotContain("private-token", display);
    }
    /// <summary>非法凭据输入不能引入头、作用域分隔符或错误回显。</summary>
    [Theory] [InlineData("key")] [InlineData("secret")] [InlineData("token")]
    public void InvalidCredentialText_FailsWithoutEcho(string field)
    {
        const string value = "private-secret\r\ninjected"; var error = Assert.Throws<ArgumentException>(() => new AwsLlmSigningCredentials("principal", field == "key" ? value : AwsCredentialSource.KeyId, field == "secret" ? value : AwsCredentialSource.Secret, new AwsClock().Now, field == "token" ? value : null)); Assert.DoesNotContain("private-secret", error.Message);
    }
}
