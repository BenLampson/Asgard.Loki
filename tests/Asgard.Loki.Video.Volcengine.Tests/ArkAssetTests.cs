using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.Volcengine.Tests.AssetFixture;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>从公开素材客户端验证固定原生协议，不执行真实上传、分页遍历或素材删除。</summary>
public sealed class ArkAssetTests
{
    /// <summary>十个操作逐一核对原始 JSON、最终地址、认证与完整结果，不转换大小写或嵌套。</summary>
    [Theory]
    [InlineData("CreateAssetGroup")] [InlineData("CreateAsset")] [InlineData("ListAssetGroups")] [InlineData("ListAssets")] [InlineData("GetAsset")]
    [InlineData("GetAssetGroup")] [InlineData("UpdateAssetGroup")] [InlineData("UpdateAsset")] [InlineData("DeleteAsset")] [InlineData("DeleteAssetGroup")]
    public async Task NativeOperation_PreservesEntireWireAndResult(string action)
    {
        var fixture = Case(action); var response = fixture.GetProperty("response");
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://ark.invalid/?Action=" + action + "&Version=2024-01-01", request.RequestUri!.AbsoluteUri);
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            using var body = JsonDocument.Parse(bytes); Assert.True(JsonElement.DeepEquals(fixture.GetProperty("request"), body.RootElement));
            Assert.Equal(9007199254740993L, body.RootElement.GetProperty("Future").GetProperty("large").GetInt64());
            Assert.Equal("application/json", request.Content.Headers.ContentType!.ToString()); Assert.Equal("ark.invalid", request.Headers.Host);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), request.Headers.GetValues("X-Content-Sha256").Single());
            Assert.StartsWith("HMAC-SHA256 Credential=fixture-access/20261020/cn-beijing/ark/request, ", request.Headers.GetValues("Authorization").Single());
            return ArkFixture.Json(response);
        });
        using var factory = new VideoHttpFactory(handler); var client = new VolcengineAssetClient(factory, Options(), new AssetTestClock());
        var actual = await Call(client, action, Request(action), TestContext.Current.CancellationToken);
        Assert.Equal(action, actual.Action); Assert.True(JsonElement.DeepEquals(response, actual.Data)); Assert.True(JsonElement.DeepEquals(response.GetProperty("Result"), actual.Result));
        Assert.Equal(1, handler.Calls); Assert.Equal(1, factory.ClientsDisposed); Assert.Equal(0, factory.Disposals);
    }
    /// <summary>完整认证头必须匹配独立 Python 向量，包括非默认端口及临时令牌参与签名。</summary>
    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task Signature_MatchesIndependentVector(int index)
    {
        var vector = Data().GetProperty("signatures")[index];
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            Assert.Equal(vector.GetProperty("body").GetString(), await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(vector.GetProperty("authorization").GetString(), request.Headers.GetValues("Authorization").Single());
            Assert.Equal(vector.GetProperty("date").GetString(), request.Headers.GetValues("X-Date").Single());
            Assert.Equal(vector.GetProperty("payload_hash").GetString(), request.Headers.GetValues("X-Content-Sha256").Single());
            Assert.Equal(vector.GetProperty("host").GetString(), request.Headers.Host);
            if (index == 1) Assert.Equal("fixture-session-token", request.Headers.GetValues("X-Security-Token").Single());
            else Assert.False(request.Headers.Contains("X-Security-Token"));
            return ArkFixture.Json(Case("GetAsset").GetProperty("response"));
        });
        var options = Options(vector.GetProperty("host").GetString()!, vector.GetProperty("session_token").GetString());
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), options, new AssetTestClock());
        await client.GetAssetAsync(JsonNode.Parse(vector.GetProperty("body").GetString()!)!.AsObject(), TestContext.Current.CancellationToken);
    }
    /// <summary>HTTP 200 不能抹除素材预处理失败，未知状态也不能转换为 Active。</summary>
    [Theory] [InlineData("Active")] [InlineData("Processing")] [InlineData("Failed")] [InlineData("future_state")]
    public async Task AssetStatusAndProcessingError_AreNativeData(string status)
    {
        var reply = Reply("GetAsset"); reply["Result"]!["Status"] = status;
        reply["Result"]!["Error"] = new JsonObject { ["Code"] = "fixture-processing-error", ["Message"] = "素材未完成🌋" };
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(reply.ToJsonString())));
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options());
        var actual = await client.GetAssetAsync(Request("GetAsset"), TestContext.Current.CancellationToken);
        Assert.Equal(status, actual.Result.GetProperty("Status").GetString()); Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(reply), actual.Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>错误信封和 HTTP 失败保留原始字节，既不自动重试也不误标记为任务结果。</summary>
    [Theory] [InlineData(200)] [InlineData(403)]
    public async Task ApiError_PreservesOriginalBytes(int status)
    {
        const string body = "{\"ResponseMetadata\":{\"RequestId\":\"fixture\",\"Error\":{\"Code\":\"Denied\",\"Message\":\"权限不足\"}},\"Future\":false}";
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(ArkFixture.Json(body, (HttpStatusCode)status)));
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options());
        var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => client.GetAssetAsync(Request("GetAsset"), TestContext.Current.CancellationToken));
        Assert.Equal(status, (int)error.StatusCode); Assert.Equal(Encoding.UTF8.GetBytes(body), error.ResponseBody.ToArray()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>调用在等待网络前复制原生请求，调用者后续修改不能改变签名正文或预期资源。</summary>
    [Fact]
    public async Task Snapshot_PrecedesTransportWait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = Request("GetAsset"); var expected = input.DeepClone();
        using var handler = new StubHttpHandler(async (request, ct) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(ct);
            var wire = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct)); Assert.True(JsonNode.DeepEquals(expected, wire));
            return ArkFixture.Json(Case("GetAsset").GetProperty("response"));
        });
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options());
        var pending = client.GetAssetAsync(input, TestContext.Current.CancellationToken); await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        input["Id"] = "other"; input["Future"]!["large"] = 1; release.SetResult();
        var result = await pending; Assert.Equal("asset-fixture-01", result.Result.GetProperty("Id").GetString());
    }
    /// <summary>页码分页长整数与 NextToken 分页分别保留，不自动遍历或注入另一种参数。</summary>
    [Fact]
    public async Task PageNumberMode_RemainsOriginalAndSinglePage()
    {
        var request = Request("ListAssets"); request.Remove("MaxResults"); request.Remove("NextToken"); request["PageNumber"] = 2L; request["PageSize"] = 1L;
        var reply = Reply("ListAssets"); reply["Result"]!.AsObject().Remove("NextToken"); reply["Result"]!["PageNumber"] = 2; reply["Result"]!["PageSize"] = 1; reply["Result"]!["TotalCount"] = 10;
        using var handler = new StubHttpHandler(async (message, ct) =>
        { Assert.True(JsonNode.DeepEquals(request, JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct)))); return ArkFixture.Json(reply.ToJsonString()); });
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options()); var actual = await client.ListAssetsAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(10, actual.Result.GetProperty("TotalCount").GetInt32()); Assert.Equal(1, handler.Calls);
    }
    /// <summary>官方素材创建示例允许省略名称和项目，SDK 不能拒绝或补写这些原生可选字段。</summary>
    [Fact]
    public async Task CreateAsset_LeavesOptionalNameAndProjectAbsent()
    {
        var request = Request("CreateAsset"); request.Remove("Name"); request.Remove("ProjectName");
        using var handler = new StubHttpHandler(async (message, ct) =>
        {
            var wire = JsonNode.Parse(await message.Content!.ReadAsStringAsync(ct))!.AsObject();
            Assert.False(wire.ContainsKey("Name")); Assert.False(wire.ContainsKey("ProjectName")); Assert.True(JsonNode.DeepEquals(request, wire));
            return ArkFixture.Json(Case("CreateAsset").GetProperty("response"));
        });
        var client = new VolcengineAssetClient(new StubHttpClientFactory(handler), Options());
        await client.CreateAssetAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, handler.Calls);
    }
}
