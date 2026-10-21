using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Volcengine.Tests.ArkFixture;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>原生完整结果必须等待正常 EOF、长度校验、释放和取消检查。</summary>
public sealed class ArkVideoTransportTests
{
    /// <summary>完整窗口在所有传输资源成功释放后才返回，工厂保持借用。</summary>
    [Fact]
    public async Task Success_IsPublishedAfterClientAndBodyDisposal()
    {
        var body = new VideoBodyStream(Encoding.UTF8.GetBytes(Query(6).GetRawText()));
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Response(body))); var factory = new VideoHttpFactory(handler);
        var adapter = new VolcengineVideoAdapter(factory,Options()); var result = await adapter.GetAsync("task_ark_one",TestContext.Current.CancellationToken);
        Assert.True(result.IsTerminal); Assert.True(body.Disposed); Assert.Equal(1,factory.ClientsDisposed); Assert.Equal(0,factory.Disposals); Assert.Equal(1,handler.Calls);
    }
    /// <summary>JSON 正文结束后的读取或释放错误仍阻止成功，不自动发出第二次请求。</summary>
    [Theory] [InlineData("read")] [InlineData("body-dispose")] [InlineData("client-dispose")]
    public async Task LateResourceFault_IsNotSuccessfulTask(string kind)
    {
        var body = new VideoBodyStream(Encoding.UTF8.GetBytes(Query(6).GetRawText())) { FailAtEof = kind == "read", FailDispose = kind == "body-dispose" };
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Response(body)));
        var factory = new VideoHttpFactory(handler) { OnClientDispose = () => { if (kind == "client-dispose") throw new IOException("late client disposal"); } };
        var adapter = new VolcengineVideoAdapter(factory,Options());
        await Assert.ThrowsAsync<IOException>(() => adapter.GetAsync("task_ark_one",TestContext.Current.CancellationToken));
        Assert.True(body.Disposed); Assert.Equal(1,factory.ClientsDisposed); Assert.Equal(0,factory.Disposals); Assert.Equal(1,handler.Calls);
    }
    /// <summary>最终读取或客户端释放发生取消时不得先返回成功。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalCancellation_IsNotSuccessfulTask(bool duringDispose)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new VideoBodyStream(Encoding.UTF8.GetBytes(Query(6).GetRawText())) { OnEof = () => { if (!duringDispose) cancel.Cancel(); } };
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Response(body)));
        var factory = new VideoHttpFactory(handler) { OnClientDispose = () => { if (duringDispose) cancel.Cancel(); } };
        var adapter = new VolcengineVideoAdapter(factory,Options());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("task_ark_one",cancel.Token)); Assert.True(body.Disposed); Assert.Equal(1,handler.Calls);
    }
    /// <summary>声明长度不符和原始字节超限不能在解析到有效 JSON 后被忽略。</summary>
    [Theory] [InlineData(-1)] [InlineData(1)] [InlineData(0)]
    public async Task LengthAndResponseLimits_AreCheckedBeforeResult(int difference)
    {
        var bytes = Encoding.UTF8.GetBytes(Query(6).GetRawText()); var body = new VideoBodyStream(bytes);
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Response(body,difference == 0 ? null : bytes.Length+difference)));
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options(responseLimit:difference == 0 ? 10 : bytes.Length+10));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("task_ark_one",TestContext.Current.CancellationToken)); Assert.True(body.Disposed); Assert.Equal(1,handler.Calls);
    }
    /// <summary>无效编码、重复属性、无效字符串和损坏 JSON 均为协议失败。</summary>
    [Theory] [InlineData("utf8")] [InlineData("duplicate")] [InlineData("escape")] [InlineData("truncated")] [InlineData("array")]
    public async Task InvalidJsonAndEncoding_FailExplicitly(string kind)
    {
        var data = kind switch
        {
            "duplicate"=>Encoding.UTF8.GetBytes("{\"id\":\"task_ark_one\",\"id\":\"other\",\"status\":\"queued\"}"),
            "escape"=>Encoding.UTF8.GetBytes("{\"id\":\"task_ark_one\",\"status\":\"queued\",\"future\":\"\\uD800\"}"),
            "truncated"=>Encoding.UTF8.GetBytes("{\"id\":\"task_ark_one\","), "array"=>Encoding.UTF8.GetBytes("[]"), _=>new byte[] { 0xff }
        };
        using var handler = new StubHttpHandler((_,_) => Task.FromResult(Response(new VideoBodyStream(data))));
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("task_ark_one",TestContext.Current.CancellationToken)); Assert.Equal(1,handler.Calls);
    }
    /// <summary>无效原生输入字符在序列化替代发生之前被拒绝，不改变素材或工具字段。</summary>
    [Fact]
    public async Task InvalidInputUnicode_IsRejectedBeforeHttp()
    {
        using var handler = new StubHttpHandler((_,_) => throw new InvalidOperationException()); var request = Request(); request.Parameters["future"] = "\ud800";
        var adapter = new VolcengineVideoAdapter(new StubHttpClientFactory(handler),Options());
        await Assert.ThrowsAsync<EncoderFallbackException>(() => adapter.SubmitAsync(request,TestContext.Current.CancellationToken)); Assert.Equal(0,handler.Calls);
    }
    /// <summary>秘密不出现在配置 JSON、字符串或公开目标摘要中。</summary>
    [Fact]
    public void CredentialAndInvalidHeader_DoNotExposeSecret()
    {
        using var handler = new StubHttpHandler((_,_) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler); var options = Options();
        var adapter = new VolcengineVideoAdapter(factory,options);
        Assert.DoesNotContain(options.ApiKey,JsonSerializer.Serialize(options)); Assert.DoesNotContain(options.ApiKey,adapter.BindingIdentity); Assert.DoesNotContain(options.ApiKey,options.ToString()!);
        var error = Assert.Throws<ArgumentException>(() => new VolcengineVideoAdapter(factory,Options(key:"fake-sensitive\r\nvalue")));
        Assert.DoesNotContain("fake-sensitive",error.ToString()); Assert.Equal(0,handler.Calls);
    }
    /// <summary>每个响应拥有自己的流，不隐式转存或重放内容。</summary>
    private static HttpResponseMessage Response(Stream body,long? length = null)
    {
        var content = new StreamContent(body); content.Headers.ContentType = new("application/json");
        if (length is { } size) content.Headers.ContentLength = size;
        return new(HttpStatusCode.OK) { Content = content };
    }
}
