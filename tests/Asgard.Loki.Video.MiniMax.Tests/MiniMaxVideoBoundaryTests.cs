using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.MiniMax.Tests.HailuoFixture;

namespace Asgard.Loki.Video.MiniMax.Tests;

/// <summary>原生响应、结果关联、传输上限、资源释放和最终取消的离线边界。</summary>
public sealed class MiniMaxVideoBoundaryTests
{
    /// <summary>缺失身份、错误信封或不完整成功都不能变成可用任务。</summary>
    [Theory] [InlineData(MiniMaxVideoApiVersion.V1, "id")] [InlineData(MiniMaxVideoApiVersion.V1, "status")]
    [InlineData(MiniMaxVideoApiVersion.V1, "foreign")] [InlineData(MiniMaxVideoApiVersion.V1, "output")] [InlineData(MiniMaxVideoApiVersion.V1, "base")]
    [InlineData(MiniMaxVideoApiVersion.V2, "id")] [InlineData(MiniMaxVideoApiVersion.V2, "status")] [InlineData(MiniMaxVideoApiVersion.V2, "foreign")]
    [InlineData(MiniMaxVideoApiVersion.V2, "output")] [InlineData(MiniMaxVideoApiVersion.V2, "task")]
    public async Task InvalidTask_FailsExplicitly(MiniMaxVideoApiVersion version, string kind)
    {
        var data = JsonNode.Parse(Query(version, 5).GetRawText())!.AsObject(); var task = version == MiniMaxVideoApiVersion.V1 ? data : data["task"]!.AsObject();
        if (kind == "id") task.Remove(version == MiniMaxVideoApiVersion.V1 ? "task_id" : "id");
        else if (kind == "status") task["status"] = true;
        else if (kind == "foreign") task[version == MiniMaxVideoApiVersion.V1 ? "task_id" : "id"] = "other-task";
        else if (kind == "output") task.Remove(version == MiniMaxVideoApiVersion.V1 ? "file_id" : "content");
        else if (kind == "base") data.Remove("base_resp"); else data["task"] = new JsonArray();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(version));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync(version == MiniMaxVideoApiVersion.V1 ? "hailuo-v1-01" : "hailuo-v2-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>只有原生数字状态码有效，错误类型或缺失码不能被宽松转换成成功。</summary>
    [Theory] [InlineData("{}")] [InlineData("{\"status_code\":\"0\"}")] [InlineData("{\"status_code\":false}")] [InlineData("{\"status_code\":0.5}")]
    public async Task InvalidBaseResponse_FailsExplicitly(string status)
    {
        var data = JsonNode.Parse(Query(MiniMaxVideoApiVersion.V1, 0).GetRawText())!; data["base_resp"] = JsonNode.Parse(status);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("hailuo-v1-01", TestContext.Current.CancellationToken));
    }
    /// <summary>v2 拒绝仅有 v1 字段的成功；v1 也不能用 v2 地址代替原生 file_id。</summary>
    [Theory] [InlineData(MiniMaxVideoApiVersion.V1)] [InlineData(MiniMaxVideoApiVersion.V2)]
    public async Task SuccessfulResult_CannotUseAnotherVersionShape(MiniMaxVideoApiVersion version)
    {
        var data = JsonNode.Parse(Query(version, 5).GetRawText())!;
        if (version == MiniMaxVideoApiVersion.V1) { data.AsObject().Remove("file_id"); data["content"] = new JsonObject { ["url"] = "https://media.invalid/video" }; }
        else { data["task"]!.AsObject().Remove("content"); data["task"]!["file_id"] = "9007199254740993"; }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(version));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync(version == MiniMaxVideoApiVersion.V1 ? "hailuo-v1-01" : "hailuo-v2-01", TestContext.Current.CancellationToken));
    }
    /// <summary>非法 UTF-8、未知成员中的损坏字符串、重复属性和非 JSON 响应均失败。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("escape")] [InlineData("array")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidWireBytes_FailExplicitly(string kind)
    {
        var bytes = kind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"task_id\":\"a\",\"task_id\":\"b\"}"), "utf8" => new byte[] { 0xff },
            "escape" => Encoding.UTF8.GetBytes("{\"task_id\":\"hailuo-v2-01\",\"Future\":\"\\uD800\"}"), "array" => Encoding.UTF8.GetBytes("[]"),
            "truncated" => Encoding.UTF8.GetBytes("{\"task_id\":"), _ => Encoding.UTF8.GetBytes(Data().GetProperty("submissions")[2].GetProperty("response").GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "application/jsonp" : "application/json"); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        });
        var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(MiniMaxVideoApiVersion.V2));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.SubmitAsync(Request(2), TestContext.Current.CancellationToken));
    }
    /// <summary>请求保留模型字段、损坏编码和 v2 内容容器错误在 HTTP 前失败。</summary>
    [Theory] [InlineData("model")] [InlineData("content")] [InlineData("empty")] [InlineData("member")]
    public async Task InvalidNativeRequest_FailsBeforeIo(string kind)
    {
        var request = Request(2);
        if (kind == "model") request.Parameters["model"] = "other"; else if (kind == "content") request.Parameters.Remove("content");
        else request.Parameters["content"] = kind == "empty" ? new JsonArray() : new JsonArray(1);
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(MiniMaxVideoApiVersion.V2));
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>原生文件 ID 不允许浮点数、越界或非十进制字符，避免关联错误和精度丢失。</summary>
    [Theory] [InlineData("0")] [InlineData("001")] [InlineData("1e3")] [InlineData("-1")] [InlineData("9223372036854775808")]
    public async Task InvalidVideoFileId_DoesNotBecomeSuccess(string id)
    {
        var data = JsonNode.Parse(Query(MiniMaxVideoApiVersion.V1, 5).GetRawText())!; data["file_id"] = id;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync("hailuo-v1-01", TestContext.Current.CancellationToken));
    }
    /// <summary>原始字节上限、无效输入 Unicode 与预取消明确生效，不重试。</summary>
    [Fact]
    public async Task LimitsEncodingAndCancellation_AreEnforced()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(MiniMaxVideoApiVersion.V1, 0)))); var factory = new StubHttpClientFactory(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new MiniMaxVideoAdapter(factory, Options(requestLimit: 1)).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        var request = Request(); request.Parameters["Future"] = "\ud800"; await Assert.ThrowsAsync<EncoderFallbackException>(() => new MiniMaxVideoAdapter(factory, Options()).SubmitAsync(request, TestContext.Current.CancellationToken));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var adapter = new MiniMaxVideoAdapter(factory, Options());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync("task", cancel.Token)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetResultFileAsync(Completed(), cancel.Token)); Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new MiniMaxVideoAdapter(factory, Options(responseLimit: 1)).GetAsync("hailuo-v1-01", TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>短期调用完成后的资源释放和取消仍属于完整原生操作，不能提前返回任务或结果文件。</summary>
    [Theory] [InlineData("submit", false)] [InlineData("submit", true)] [InlineData("query", false)] [InlineData("query", true)] [InlineData("file", false)] [InlineData("file", true)]
    public async Task FinalDisposalFaultOrCancellation_PreventsResult(string operation, bool cancel)
    {
        using var cancellation = new CancellationTokenSource(); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(operation switch
        { "submit" => Data().GetProperty("submissions")[0].GetProperty("response"), "file" => Data().GetProperty("file_results")[0], _ => Query(MiniMaxVideoApiVersion.V1, 5) })));
        using var factory = new MiniMaxTestHttpFactory(handler) { OnClientDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture disposal"); } }; var adapter = new MiniMaxVideoAdapter(factory, Options());
        async Task Call() { if (operation == "submit") await adapter.SubmitAsync(Request(), cancellation.Token); else if (operation == "file") await adapter.GetResultFileAsync(Completed(), cancellation.Token); else await adapter.GetAsync("hailuo-v1-01", cancellation.Token); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Call); else await Assert.ThrowsAsync<IOException>(Call);
        Assert.Equal(1, handler.Calls); Assert.False(factory.Disposed);
    }
    /// <summary>调用创建的客户端按次释放，工厂与模块外适配器仍由调用者拥有。</summary>
    [Fact]
    public async Task BorrowedFactory_RemainsCallerOwned()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Data().GetProperty("submissions")[0].GetProperty("response")))); using var factory = new MiniMaxTestHttpFactory(handler);
        var loki = Build(factory); await loki.Video.SubmitAsync(Request(), TestContext.Current.CancellationToken); await loki.DisposeAsync(); Assert.False(factory.Disposed); Assert.Equal(1, factory.ClientDisposals);
    }
    /// <summary>密钥不进入配置、显示或绑定摘要；非法认证和版本配置在构造时失败。</summary>
    [Fact]
    public void CredentialsAndOptions_AreExplicit()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler); var options = Options(); var adapter = new MiniMaxVideoAdapter(factory, options);
        Assert.DoesNotContain(options.ApiKey, JsonSerializer.Serialize(options)); Assert.DoesNotContain(options.ApiKey, adapter.BindingIdentity); Assert.DoesNotContain(options.ApiKey, options.ToString()!);
        Assert.Throws<ArgumentException>(() => new MiniMaxVideoAdapter(factory, Options(key: "sensitive\r\nkey"))); Assert.Throws<ArgumentOutOfRangeException>(() => new MiniMaxVideoAdapter(factory, Options(version: (MiniMaxVideoApiVersion)0)));
        Assert.Throws<ArgumentException>(() => new MiniMaxVideoAdapter(factory, Options(root: "http://hailuo.invalid/"))); Assert.Equal(0, handler.Calls);
    }
}
