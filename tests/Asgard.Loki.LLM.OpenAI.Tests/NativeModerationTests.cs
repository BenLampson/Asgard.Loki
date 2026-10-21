using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>实际公共 SDK 与原生适配器的完整 HTTP、输入边界和分类结果保真。</summary>
public sealed class NativeModerationTests
{
    /// <summary>原生文本、批次、多模态组和空文本不合并/过滤；完整标签、分数和未知项保持。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task FullNativeWireAndResultRemainOriginal(int index)
    {
        using var factory = ModerationFixture.Factory(index); using var stream = new ModerationReadStream(Encoding.UTF8.GetBytes(ModerationFixture.Part("responses")[index].GetRawText()));
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://moderation.example.invalid/tenant/v1/moderations", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme); Assert.Equal("fixture-moderation-key-a", message.Headers.Authorization.Parameter);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            var actual = ModerationFixture.Element(await message.Content.ReadAsStringAsync(token)); Assert.True(JsonElement.DeepEquals(ModerationFixture.Part("requests")[index], actual)); Assert.False(actual.TryGetProperty("stream", out _));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        };
        await using var client = ModerationFixture.Build(factory); var request = ModerationFixture.Request(index); var before = JsonSerializer.Serialize(request);
        var result = await client.LLM.Moderations.CreateAsync(request, new() { SessionId = "golden-" + index }, TestContext.Current.CancellationToken);
        Assert.Equal(before, JsonSerializer.Serialize(request)); Assert.True(JsonElement.DeepEquals(ModerationFixture.Part("responses")[index], result.Data));
        Assert.Equal(new LlmResultMetadata("public-moderation", "caller-selected-moderation-model", "openai", "a"), result.Metadata);
        Assert.Equal("native-reported-moderation-version", result.Data.GetProperty("model").GetString()); Assert.Equal(index == 1 ? 2 : 1, result.Data.GetProperty("results").GetArrayLength());
        if (index == 0)
        {
            Assert.Equal("0.123456789012345678901", result.Data.GetProperty("results")[0].GetProperty("category_scores").GetProperty("harassment").GetRawText());
            Assert.Equal(JsonValueKind.Null, result.Data.GetProperty("results")[0].GetProperty("categories").GetProperty("illicit").ValueKind); Assert.Equal("9007199254740993", result.Data.GetProperty("future").GetProperty("exact").GetRawText());
        }
        if (index == 1) { Assert.Equal("first", result.Data.GetProperty("results")[0].GetProperty("future").GetProperty("input_ref").GetString()); Assert.Equal("second", result.Data.GetProperty("results")[1].GetProperty("future").GetProperty("input_ref").GetString()); }
        Assert.True(stream.Eof); Assert.True(stream.Disposed); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed); Assert.Equal("moderation-tests", factory.ClientName);
    }
    /// <summary>工厂创建期间清空外部直接调用请求不会改变已冻结的完整 HTTP 正文。</summary>
    [Fact]
    public async Task DirectPayloadSnapshotPrecedesFactory()
    {
        var request = ModerationFixture.Payload(); using var factory = ModerationFixture.Factory(); factory.OnCreate = request.Clear;
        factory.OnSend = async (message, token) => { Assert.True(JsonElement.DeepEquals(ModerationFixture.Part("requests")[0], ModerationFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return ModerationFixture.Json(ModerationFixture.Part("responses")[0]); };
        var provider = ModerationFixture.Provider(factory); await ((IModerationsAdapter)provider).CreateAsync(request, ModerationFixture.Context(provider), TestContext.Current.CancellationToken); Assert.Empty(request);
    }
    /// <summary>HTTP 与 HTTP200 原生错误保留状态/正文/未知字段，不将重试提示变成自动请求。</summary>
    [Theory] [InlineData(200)] [InlineData(400)] [InlineData(429)] [InlineData(503)]
    public async Task NativeErrorsRetainRawBytesAndStatus(int status)
    {
        var raw = ModerationFixture.Part("native_error"); using var factory = ModerationFixture.Factory(); factory.OnSend = (_, _) =>
        { var response = ModerationFixture.Json(raw, (HttpStatusCode)status); response.Headers.TryAddWithoutValidation("Retry-After", "1"); return Task.FromResult(response); };
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ModerationFixture.Invoke(ModerationFixture.Provider(factory), 0, TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(raw.GetRawText()), error.Body.ToArray()); Assert.Equal(raw.GetRawText(), error.ResponseBody); Assert.Equal("application/json; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
    /// <summary>HTTP200 原生错误即使被标成纯文本仍保留原始空白和正文，不先归为成功媒体错误。</summary>
    [Fact]
    public async Task PlainTextLabeledNativeErrorKeepsOriginalBody()
    {
        var raw = " \n" + ModerationFixture.Part("native_error").GetRawText() + "\n "; using var factory = ModerationFixture.Factory();
        factory.OnSend = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "text/plain") });
        var error = await Assert.ThrowsAsync<LlmUpstreamException>(() => ModerationFixture.Invoke(ModerationFixture.Provider(factory), 0, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, error.StatusCode); Assert.Equal(raw, error.ResponseBody); Assert.Equal(Encoding.UTF8.GetBytes(raw), error.Body.ToArray()); Assert.Equal("text/plain; charset=utf-8", error.ContentType); Assert.Equal(1, factory.Calls);
    }
    /// <summary>绕过路由直接调用也不能将外部或匿名身份降级为 API Key。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NonApiKeyIdentitiesCannotDispatchModerations(bool external)
    {
        using var factory = ModerationFixture.Factory(); var provider = ModerationFixture.Provider(factory);
        var credential = external ? LlmCredential.External("external", "fixture-principal") : LlmCredential.Anonymous("anonymous");
        var context = new LlmAdapterContext(ModerationFixture.Target() with { CredentialId = credential.Id }, credential, "public-moderation", "session");
        await Assert.ThrowsAsync<NotSupportedException>(() => ((IModerationsAdapter)provider).CreateAsync(ModerationFixture.Payload(), context, TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }

}
