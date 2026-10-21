using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.LLM.NewAPI.Tests.SearchFixture;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>固定目标、明确鉴权与严格原生输入在任何 HTTP 前受约束。</summary>
public sealed class SearchValidationTests
{
    /// <summary>不可借用 OAuth 主体或匿名身份伪装成平台 API Key。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public void OtherCredentialKind_IsRejected(bool external)
    { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); Assert.Throws<ArgumentException>(() => new NewApiSearchClient(factory, Options(), external ? LlmCredential.External("external", "principal") : LlmCredential.Anonymous("anonymous"))); Assert.Empty(factory.Clients); }
    /// <summary>URI 必须为完整 API 根，不能隐式携带凭据或将查询片段当作配置。</summary>
    [Theory] [InlineData("relative/v1/")] [InlineData("file:///native/")] [InlineData("https://user:secret@upstream.invalid/v1/")] [InlineData("https://upstream.invalid/v1/?key=hidden")] [InlineData("https://upstream.invalid/v1/#fragment")] [InlineData("https://upstream.invalid/v1/\n")]
    public void InvalidRoot_IsRejected(string root)
    { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); Assert.Throws<ArgumentException>(() => new NewApiSearchClient(factory, Options() with { BaseAddress = new(root, UriKind.RelativeOrAbsolute) }, Credential())); Assert.Empty(factory.Clients); }
    /// <summary>模型字段由固定目标拥有，包括大小写别名，不能从参数绕过目标选择。</summary>
    [Theory] [InlineData("model")] [InlineData("Model")] [InlineData("MODEL")]
    public async Task ModelOverride_HasNoIo(string field)
    { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); var parameters = Parameters(); parameters[field] = "other-model"; var before = parameters.ToJsonString(); await Assert.ThrowsAsync<ArgumentException>(() => new NewApiSearchClient(factory, Options(), Credential()).SearchAsync(parameters, TestContext.Current.CancellationToken)); Assert.Equal(before, parameters.ToJsonString()); Assert.Empty(factory.Clients); }
    /// <summary>请求和响应预算必须显式为正数。</summary>
    [Theory] [InlineData(true, 0)] [InlineData(true, -1)] [InlineData(false, 0)] [InlineData(false, -1)]
    public void InvalidBudget_HasNoIo(bool request, int budget)
    { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); Assert.Throws<ArgumentOutOfRangeException>(() => new NewApiSearchClient(factory, request ? Options() with { MaximumRequestBytes = budget } : Options() with { MaximumResponseBytes = budget }, Credential())); Assert.Empty(factory.Clients); }
    /// <summary>非法 Unicode、重复嵌入 JSON 和别名字段不能先经序列化丢失再发送。</summary>
    [Theory] [InlineData("value")] [InlineData("name")] [InlineData("embedded-unicode")] [InlineData("embedded-duplicate")]
    public async Task InvalidNativeJson_HasNoIo(string kind)
    {
        var parameters = Parameters(); if (kind == "value") parameters["native"] = "\uD800"; else if (kind == "name") parameters["\uD800"] = 1; else { using var document = JsonDocument.Parse(kind == "embedded-unicode" ? "{\"value\":\"\\uD800\"}" : "{\"same\":1,\"same\":2}"); parameters["native"] = JsonNode.Parse(document.RootElement.GetRawText()); }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); var failure = await Record.ExceptionAsync(() => new NewApiSearchClient(factory, Options(), Credential()).SearchAsync(parameters, TestContext.Current.CancellationToken)); Assert.NotNull(failure); Assert.True(failure is ArgumentException or InvalidDataException); Assert.Empty(factory.Clients); Assert.Equal(0, handler.Calls);
    }
    /// <summary>原生 Key 是不解析的完整凭据，控制字符不能进入认证头。</summary>
    [Fact]
    public void HeaderInjection_IsRejected()
    { using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException("unexpected IO")); using var factory = new SearchFactory(handler); var failure = Record.Exception(() => new NewApiSearchClient(factory, Options(), new("key", "fixture-key\r\nx-secret: bad"))); Assert.NotNull(failure); Assert.True(failure is ArgumentException or FormatException); Assert.Empty(factory.Clients); }
}
