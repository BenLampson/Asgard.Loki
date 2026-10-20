using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Volcengine.Tests.AssetFixture;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>素材错误、输入边界和最终传输终态的离线回归。</summary>
public sealed class ArkAssetValidationTests
{
    /// <summary>错误操作、版本、区域、服务、资源 ID 和缺失结果不能交给调用者当成有效响应。</summary>
    [Theory] [InlineData("Action")] [InlineData("Version")] [InlineData("Service")] [InlineData("Region")]
    [InlineData("Id")] [InlineData("Status")] [InlineData("Result")] [InlineData("ResponseMetadata")]
    public async Task InvalidNativeReply_FailsExplicitly(string field)
    {
        var reply = Reply("GetAsset");
        if (field is "Action" or "Version" or "Service" or "Region") reply["ResponseMetadata"]![field] = "other";
        else if (field == "Id") reply["Result"]![field] = "other";
        else if (field == "Status") reply["Result"]!.AsObject().Remove(field);
        else reply.Remove(field);
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(reply.ToJsonString())));
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAssetAsync(Request("GetAsset"), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>列表必须保持 Items 数组及资源标识，不能把错误形状变成空页成功。</summary>
    [Theory] [InlineData("missing")] [InlineData("object")] [InlineData("scalar")] [InlineData("id")]
    public async Task InvalidList_IsNotAnEmptySuccessfulPage(string kind)
    {
        var reply = Reply("ListAssets"); var result = reply["Result"]!.AsObject();
        if (kind == "missing") result.Remove("Items");
        else if (kind == "object") result["Items"] = new JsonObject();
        else if (kind == "scalar") result["Items"] = new JsonArray(1);
        else result["Items"]![0]!.AsObject().Remove("Id");
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(reply.ToJsonString())));
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAssetsAsync(Request("ListAssets"), TestContext.Current.CancellationToken));
    }
    /// <summary>明显缺项及分页冲突本地失败，不产生素材创建、删除或查询请求。</summary>
    [Theory] [InlineData("mixed")] [InlineData("half-page")] [InlineData("zero")] [InlineData("fraction")] [InlineData("filter")]
    [InlineData("group-type")] [InlineData("token")] [InlineData("id")] [InlineData("name")] [InlineData("url")]
    public async Task InvalidInput_FailsBeforeHttp(string kind)
    {
        var action = kind switch { "id" => "DeleteAssetGroup", "name" => "CreateAssetGroup", "url" => "CreateAsset", _ => "ListAssets" };
        var request = Request(action);
        switch (kind)
        {
            case "mixed": request["PageNumber"] = 1; request["PageSize"] = 10; break;
            case "half-page": request.Remove("MaxResults"); request.Remove("NextToken"); request["PageNumber"] = 1; break;
            case "zero": request["MaxResults"] = 0; break;
            case "fraction": request["MaxResults"] = 1.5m; break;
            case "filter": request["Filter"] = new JsonArray(); break;
            case "group-type": request["Filter"]!.AsObject().Remove("GroupType"); break;
            case "token": request["NextToken"] = ""; break;
            case "id": request["Id"] = false; break;
            case "name": request.Remove("Name"); break;
            case "url": request.Remove("URL"); break;
        }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException());
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<ArgumentException>(() => Call(client, action, request, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>无效 Unicode、请求上限和预取消均在网络调用前失败。</summary>
    [Fact]
    public async Task EncodingLimitAndCancellation_FailBeforeHttp()
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler);
        var client = new VolcengineAssetClient(factory, Options()); var request = Request("GetAsset"); request["Future"]!["text"] = "\ud800";
        await Assert.ThrowsAsync<EncoderFallbackException>(() => client.GetAssetAsync(request, TestContext.Current.CancellationToken));
        client = new(factory, Options(requestLimit: 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAssetAsync(Request("GetAsset"), TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAssetAsync(Request("GetAsset"), cancelled.Token)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>等待读取终结及客户端释放时发生的失败或取消都不能被已解析结果掩盖。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FinalDisposalFailureOrCancellation_PreventsResult(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(Case("GetAsset").GetProperty("response"))));
        using var factory = new VideoHttpFactory(handler) { OnClientDispose = () => { if (cancel) cancellation.Cancel(); else throw new IOException("fixture-disposal"); } };
        var client = new VolcengineAssetClient(factory, Options());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAssetAsync(Request("GetAsset"), cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => client.GetAssetAsync(Request("GetAsset"), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>无效 JSON、编码、重复属性和超限正文仍通过相同严格传输边界失败。</summary>
    [Theory] [InlineData("duplicate")] [InlineData("utf8")] [InlineData("limit")] [InlineData("media-type")]
    public async Task InvalidResponseBytes_FailBeforeResult(string kind)
    {
        var bytes = kind == "utf8" ? new byte[] { 0xff } : Encoding.UTF8.GetBytes(kind == "duplicate" ? "{\"Result\":{},\"Result\":{}}" : Case("GetAsset").GetProperty("response").GetRawText());
        using var handler = new StubHttpHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes); if (kind == "media-type") content.Headers.ContentType = new("application/jsonp");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options(responseLimit: kind == "limit" ? 5 : 10000));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAssetAsync(Request("GetAsset"), TestContext.Current.CancellationToken));
    }
    /// <summary>所有认证秘密均从 JSON 和对象文本中排除，非法认证头不能泄露原始值。</summary>
    [Fact]
    public void Credentials_AreIndependentAndNotSerialized()
    {
        var options = Options(token: "fixture-session-token"); var json = JsonSerializer.Serialize(options);
        foreach (var secret in new[] { options.AccessKeyId, options.SecretAccessKey, options.SecurityToken! })
        { Assert.DoesNotContain(secret, json); Assert.DoesNotContain(secret, options.ToString()!); }
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var factory = new StubHttpClientFactory(handler);
        var error = Assert.Throws<ArgumentException>(() => new VolcengineAssetClient(factory, new() { AccessKeyId = "fixture-access", SecretAccessKey = "sensitive\r\nvalue" }));
        Assert.DoesNotContain("sensitive", error.ToString()); Assert.Throws<ArgumentException>(() => new VolcengineAssetClient(factory, new())); Assert.Equal(0, handler.Calls);
    }
}
