using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Google.Tests.VeoFixture;

namespace Asgard.Loki.Video.Google.Tests;

/// <summary>严格校验原生长操作形状、目标身份和编码边界。</summary>
public sealed class GoogleVideoBoundaryTests
{
    /// <summary>空结果、伪 SDK 字段、矛盾终态、错误类型及其他操作均不返回可用终态。</summary>
    [Theory] [InlineData("name")] [InlineData("other-operation")] [InlineData("other-model")] [InlineData("done-string")]
    [InlineData("response-before-done")] [InlineData("error-before-done")] [InlineData("both")]
    [InlineData("missing-result")] [InlineData("sdk-result-name")] [InlineData("empty-samples")] [InlineData("missing-uri")]
    [InlineData("bad-error-code")] [InlineData("bad-filter-count")]
    public async Task InvalidNativeOperation_FailsExplicitly(string kind)
    {
        var data = JsonNode.Parse(Query(4).GetRawText())!.AsObject();
        switch (kind)
        {
            case "name": data.Remove("name"); break;
            case "other-operation": data["name"] = Operation + "-other"; break;
            case "other-model": data["name"] = "models/other/operations/fixture-01"; break;
            case "done-string": data["done"] = "true"; break;
            case "response-before-done": data.Remove("done"); break;
            case "error-before-done": data.Remove("done"); data.Remove("response"); data["error"] = new JsonObject { ["code"] = 8 }; break;
            case "both": data["error"] = new JsonObject { ["code"] = 8 }; break;
            case "missing-result": data.Remove("response"); break;
            case "sdk-result-name": var response = data["response"]!["generateVideoResponse"]!.AsObject(); response["generatedVideos"] = response["generatedSamples"]!.DeepClone(); response.Remove("generatedSamples"); break;
            case "empty-samples": data["response"]!["generateVideoResponse"]!["generatedSamples"] = new JsonArray(); break;
            case "missing-uri": data["response"]!["generateVideoResponse"]!["generatedSamples"]![0]!["video"]!.AsObject().Remove("uri"); break;
            case "bad-error-code": data.Remove("response"); data["error"] = new JsonObject { ["code"] = "8" }; break;
            default: data["response"]!["generateVideoResponse"] = new JsonObject { ["raiMediaFilteredCount"] = "1" }; break;
        }
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
    }
    /// <summary>重复属性、未知字段中的损坏字符串和非原生 JSON 媒体类型不能被宽松解码掩盖。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("escape")] [InlineData("array")] [InlineData("truncated")] [InlineData("media-type")]
    public async Task InvalidJsonBytes_FailExplicitly(string kind)
    {
        var bytes = kind switch
        {
            "duplicate" => Encoding.UTF8.GetBytes("{\"name\":\"a\",\"name\":\"b\"}"), "utf8" => new byte[] { 0xff },
            "escape" => Encoding.UTF8.GetBytes("{\"name\":\"" + Operation + "\",\"Future\":\"\\uD800\"}"), "array" => Encoding.UTF8.GetBytes("[]"),
            "truncated" => Encoding.UTF8.GetBytes("{\"name\":"), _ => Encoding.UTF8.GetBytes(Query(0).GetRawText())
        };
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(kind == "media-type" ? "text/html" : "application/json");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        });
        var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetAsync(Operation, TestContext.Current.CancellationToken));
    }
    /// <summary>操作名称只允许当前模型下的一段原生标识，不接受 URL 或路径穿越。</summary>
    [Theory] [InlineData("https://other.invalid/task")] [InlineData("models/other/operations/a")]
    [InlineData("models/veo-3.1-generate-preview/operations/..")]
    [InlineData("models/veo-3.1-generate-preview/operations/%2e%2e")]
    [InlineData("models/veo-3.1-generate-preview/operations/a?key=wrong")]
    public async Task InvalidOperationName_FailsBeforeIo(string operation)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetAsync(operation, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>本地容器、编码、字节限制与取消检查不产生 HTTP 请求。</summary>
    [Theory] [InlineData("instances")] [InlineData("empty")] [InlineData("array-member")] [InlineData("parameters")] [InlineData("model")]
    public async Task InvalidNativeRequest_FailsBeforeIo(string kind)
    {
        var request = Request();
        if (kind == "instances") request.Parameters.Remove("instances");
        else if (kind == "empty") request.Parameters["instances"] = new JsonArray();
        else if (kind == "array-member") request.Parameters["instances"] = new JsonArray(1);
        else request.Parameters[kind] = new JsonArray();
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new GoogleVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>选项与身份摘要不泄露凭据；非法头、模型和不安全根地址在构造时拒绝。</summary>
    [Fact]
    public void Options_AreExplicitAndDoNotSerializeSecrets()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler); var options = Options(); var adapter = new GoogleVideoAdapter(factory, options);
        Assert.DoesNotContain(options.ApiKey, JsonSerializer.Serialize(options)); Assert.DoesNotContain(options.ApiKey, adapter.BindingIdentity);
        Assert.Throws<ArgumentException>(() => new GoogleVideoAdapter(factory, Options(key: "secret\r\nfixture")));
        Assert.Throws<ArgumentException>(() => new GoogleVideoAdapter(factory, Options(model: "models/veo")));
        Assert.Throws<ArgumentException>(() => new GoogleVideoAdapter(factory, Options(root: "http://veo.invalid/v1beta/")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleVideoAdapter(factory, Options(redirects: 11)));
    }
    /// <summary>原始字节上限、预取消与无效 Unicode 均可观测，不重试。</summary>
    [Fact]
    public async Task ExplicitLimitsAndCancellation_AreEnforced()
    {
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(Query(0)))); var factory = new StubHttpClientFactory(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new GoogleVideoAdapter(factory, Options(requestLimit: 1)).SubmitAsync(Request(), TestContext.Current.CancellationToken));
        var request = Request(); request.Parameters["unknown"] = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => new GoogleVideoAdapter(factory, Options()).SubmitAsync(request, TestContext.Current.CancellationToken));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GoogleVideoAdapter(factory, Options()).GetAsync(Operation, cancel.Token)); Assert.Equal(0, handler.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GoogleVideoAdapter(factory, Options(responseLimit: 1)).GetAsync(Operation, TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
}
