using System.Text;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.GoogleVertex.Tests.VertexVideoFixture;

namespace Asgard.Loki.Video.GoogleVertex.Tests;

/// <summary>原生操作形状、完整资源名、编码和字节上限的离线边界测试。</summary>
public sealed class VertexVideoBoundaryTests
{
    /// <summary>字段丢失、跨操作结果或不完整终态均失败，不能交付成功快照。</summary>
    [Theory] [InlineData("name")] [InlineData("other-operation")] [InlineData("other-project")] [InlineData("done-string")]
    [InlineData("response-before-done")] [InlineData("error-before-done")] [InlineData("both")] [InlineData("missing-result")]
    [InlineData("gemini-result-shape")] [InlineData("empty-videos")] [InlineData("missing-output")] [InlineData("bad-error-code")] [InlineData("bad-filter-count")]
    public async Task InvalidOperation_FailsExplicitly(string kind)
    {
        var data = JsonNode.Parse(Query(4).GetRawText())!.AsObject();
        switch (kind)
        {
            case "name": data.Remove("name"); break;
            case "other-operation": data["name"] = Operation + "-other"; break;
            case "other-project": data["name"] = Operation.Replace("fixture-project", "other-project"); break;
            case "done-string": data["done"] = "true"; break;
            case "response-before-done": data.Remove("done"); break;
            case "error-before-done": data.Remove("response"); data.Remove("done"); data["error"] = new JsonObject { ["code"] = 8 }; break;
            case "both": data["error"] = new JsonObject { ["code"] = 8 }; break;
            case "missing-result": data.Remove("response"); break;
            case "gemini-result-shape": data["response"] = new JsonObject { ["generateVideoResponse"] = new JsonObject { ["generatedSamples"] = new JsonArray(new JsonObject { ["video"] = new JsonObject { ["uri"] = "https://media.invalid/video" } }) } }; break;
            case "empty-videos": data["response"]!["videos"] = new JsonArray(); break;
            case "missing-output": data["response"]!["videos"] = new JsonArray(new JsonObject { ["mimeType"] = "video/mp4" }); break;
            case "bad-error-code": data.Remove("response"); data["error"] = new JsonObject { ["code"] = "8" }; break;
            default: data["response"] = new JsonObject { ["raiMediaFilteredCount"] = "1" }; break;
        }
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString())));
        var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
    }
    /// <summary>重复属性、未知字段的损坏字符串、截断和无效媒体类型不能被忽略。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("escape")] [InlineData("array")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidWireBytes_FailExplicitly(string kind)
    {
        var bytes = kind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"name\":\"a\",\"name\":\"b\"}"), "utf8" => new byte[] { 0xff },
            "escape" => Encoding.UTF8.GetBytes("{\"name\":\"" + Operation + "\",\"Future\":\"\\uD800\"}"),
            "array" => Encoding.UTF8.GetBytes("[]"), "truncated" => Encoding.UTF8.GetBytes("{\"name\":"), _ => Encoding.UTF8.GetBytes(Query(0).GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "text/html" : "application/json");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        });
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
    }
    /// <summary>不允许查询跨项目、区域、模型或编码路径，不能因此取得令牌。</summary>
    [Theory] [InlineData("project")] [InlineData("location")] [InlineData("model")] [InlineData("dot")]
    [InlineData("encoded")] [InlineData("query")] [InlineData("url")]
    public async Task InvalidResourceBinding_FailsBeforeAuthentication(string kind)
    {
        var operation = kind switch
        {
            "project" => Operation.Replace("fixture-project", "other-project"), "location" => Operation.Replace("us-central1", "other-region"),
            "model" => Operation.Replace("veo-3.1-generate-001", "other-model"), "dot" => Resource + "/operations/..", "encoded" => Resource + "/operations/%2e%2e",
            "query" => Operation + "?key=unbound", _ => "https://other.invalid/operation"
        };
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock);
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetAsync(operation, TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }
    /// <summary>本地正文容器和保留字段错误在认证前失败。</summary>
    [Theory] [InlineData("missing")] [InlineData("empty")] [InlineData("member")] [InlineData("parameters")] [InlineData("model")]
    public async Task InvalidRequest_FailsBeforeAuthentication(string kind)
    {
        var request = Request();
        if (kind == "missing") request.Parameters.Remove("instances"); else if (kind == "empty") request.Parameters["instances"] = new JsonArray();
        else if (kind == "member") request.Parameters["instances"] = new JsonArray(1); else request.Parameters[kind] = new JsonArray();
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var adapter = new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, Options(), clock);
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, source.Calls); Assert.Equal(0, handler.Calls);
    }
    /// <summary>非法资源、根地址与请求头配置在构造时失败，不推断实际资源。</summary>
    [Theory] [InlineData("bare-model")] [InlineData("publisher")] [InlineData("endpoint")] [InlineData("root")]
    [InlineData("quota")] [InlineData("request-limit")] [InlineData("response-limit")]
    public void InvalidOptions_AreRejected(string kind)
    {
        var options = kind switch
        {
            "bare-model" => Options(resource: "veo-3.1-generate-001"), "publisher" => Options(resource: Resource.Replace("publishers/google", "publishers/anthropic")),
            "endpoint" => Options(resource: "projects/p/locations/l/endpoints/e"), "root" => Options(root: "http://vertex.invalid/v1/"),
            "quota" => Options(quota: "unbound\r\nproject"), "request-limit" => Options(requestLimit: 0), _ => Options(responseLimit: 0)
        };
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        Assert.ThrowsAny<ArgumentException>(() => new GoogleVertexVideoAdapter(new StubHttpClientFactory(handler), source, options, clock)); Assert.Equal(0, source.Calls);
    }
    /// <summary>原生字节上限、无效编码和预取消不能被认证等待或读取掩盖。</summary>
    [Fact]
    public async Task LimitsEncodingAndCancellation_AreEnforced()
    {
        var clock = new VertexVideoClock(); using var source = new VertexVideoTokenSource(clock); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0)))); var factory = new StubHttpClientFactory(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVertexVideoAdapter(factory, source, Options(requestLimit: 1), clock).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        var request = Request(); request.Parameters["Unknown"] = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new GoogleVertexVideoAdapter(factory, source, Options(), clock).SubmitAsync(request, TestContext.Current.CancellationToken));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GoogleVertexVideoAdapter(factory, source, Options(), clock).GetAsync(Operation, cancel.Token)); Assert.Equal(0, source.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GoogleVertexVideoAdapter(factory, source, Options(responseLimit: 1), clock).GetAsync(Operation, TestContext.Current.CancellationToken)); Assert.Equal(1, source.Calls); Assert.Equal(1, handler.Calls);
    }
}
