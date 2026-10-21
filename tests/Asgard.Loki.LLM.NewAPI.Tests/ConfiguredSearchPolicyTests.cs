using System.Text.Json;
using Asgard.Loki.LLM.Ollama.Tests;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.NewAPI.Tests.ConfiguredSearchFixture;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>固定搜索配置先验证，实际线路不接受客户端默认值或隐藏改写。</summary>
public sealed class ConfiguredSearchPolicyTests
{
    /// <summary>只检查本次新配置的真实无效边界，不扩展其他协议矩阵。</summary>
    public static IEnumerable<object[]> InvalidConfiguration()
    {
        foreach (var path in new[] { "", " ", "//other.invalid/search", "https://other.invalid/search", "x#fragment", "x/../search", "x/%2e%2e/search", "x?value=%FF", "x?value=%GG", "x%0A", "{model}/search" }) yield return [Options() with { Transport = new() { SearchPath = path } }];
        foreach (var name in new[] { "", "bad name", "Host", "Content-Type", "Accept", "Connection", "x-key\r\nnext" }) yield return [Options(2) with { Transport = Options(2).Transport! with { CredentialName = name } }];
        foreach (var query in new[] { "api_key=x", "%61pi_key=x", "API_KEY=x" }) yield return [Options(3) with { Transport = Options(3).Transport! with { SearchPath = "search?"+query } }];
        yield return [Options() with { Authentication = (NewApiSearchAuthentication)99 }]; yield return [Options() with { Transport = new() { CredentialName = "x-key" } }]; yield return [Options(2) with { Transport = null }]; yield return [Options(3) with { Transport = null }];
    }
    /// <summary>无效配置不创建客户端、不接触实际 HTTP。</summary>
    [Theory] [MemberData(nameof(InvalidConfiguration))]
    public void InvalidConfiguration_FailsBeforeFactory(NewApiSearchOptions options)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); Assert.IsAssignableFrom<ArgumentException>(Record.Exception(() => new NewApiSearchClient(factory, options, options.Authentication == NewApiSearchAuthentication.None ? Credential(1) : Credential(0)))); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>无认证必须明确，不能把外部账户或遗漏 Key 当作降级信号。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CredentialMismatch_IsRejected(int profile)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); var mismatch = profile == 1 ? Credential(0) : Credential(1); Assert.Throws<ArgumentException>(() => new NewApiSearchClient(factory, Options(profile), mismatch)); Assert.Throws<ArgumentException>(() => new NewApiSearchClient(factory, Options(profile), LlmCredential.External("key", "principal"))); Assert.Empty(factory.Clients);
    }
    /// <summary>实际方法、URI、Key 或最后关闭时的 Host 改变均不能交付字节窗口。</summary>
    [Theory] [InlineData("url")] [InlineData("method")] [InlineData("key")] [InlineData("late")]
    public async Task WireMutation_IsRejected(string field)
    {
        HttpRequestMessage? actual = null; using var handler = new StubHttpHandler((message, _) => { actual = message; if (field == "url") message.RequestUri = new("https://other.invalid/search"); else if (field == "method") message.Method = HttpMethod.Get; else if (field == "key") { message.Headers.Remove("x-search-key"); message.Headers.Add("x-search-key", "changed"); } return Task.FromResult(SearchFixture.Reply(new OllamaBodyStream([]))); }); using var factory = new SearchFactory(handler) { OnDispose = () => { if (field == "late") actual!.Headers.Host = "other.invalid"; } }; await Assert.ThrowsAsync<InvalidDataException>(() => new NewApiSearchClient(factory, Options(2), Credential(2)).SearchAsync(Parameters(), TestContext.Current.CancellationToken)); Assert.True(Assert.Single(factory.Clients).Disposed); Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>新的明确传输拒绝未选择的默认认证，且不修改借用工厂设置。</summary>
    [Theory] [InlineData(0, "x-goog-api-key")] [InlineData(1, "Cookie")] [InlineData(2, "Authorization")] [InlineData(3, "x-api-key")]
    public async Task UnselectedDefaultCredential_IsRejected(int profile, string header)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler) { Configure = client => client.DefaultRequestHeaders.Add(header, "unselected") }; await Assert.ThrowsAsync<InvalidOperationException>(() => new NewApiSearchClient(factory, Options(profile), Credential(profile)).SearchAsync(Parameters(), TestContext.Current.CancellationToken)); Assert.Equal("unselected", Assert.Single(Assert.Single(factory.Clients).DefaultRequestHeaders.GetValues(header))); Assert.Equal(0, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>默认新增配置及元数据字段保持省略；默认地址和静态 Key 契约不改变。</summary>
    [Fact]
    public void DefaultMetadataAndPolicies_PreserveOmission()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); var options = SearchFixture.Options(); var client = new NewApiSearchClient(factory, options, SearchFixture.Credential()); var json = JsonSerializer.Serialize(client.Metadata); Assert.DoesNotContain("Authentication", json); Assert.DoesNotContain("Transport", json); Assert.DoesNotContain("Authentication", JsonSerializer.Serialize(options)); Assert.DoesNotContain("Transport", JsonSerializer.Serialize(options)); Assert.Null(client.Metadata.Authentication); Assert.Null(client.Metadata.Transport); Assert.Empty(factory.Clients);
    }
}
