using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Testing;
using Xunit;
using static Asgard.Loki.Video.MiniMax.Tests.HailuoFixture;

namespace Asgard.Loki.Video.MiniMax.Tests;

/// <summary>结果文件入口只服务成功 v1 视频任务，保留完整元数据和精确标识。</summary>
public sealed class MiniMaxVideoResultTests
{
    /// <summary>原生字符串和 int64 标识都不经过浮点数，不跟随签名地址或泄露 Key 给 CDN。</summary>
    [Theory] [InlineData(0)] [InlineData(1)]
    public async Task VideoResultMetadata_PreservesNativeTypesAndDoesNotDownload(int index)
    {
        var native = Data().GetProperty("file_results")[index];
        using var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https://hailuo.invalid/v1/files/retrieve?file_id=9007199254740993", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-hailuo-key", request.Headers.Authorization!.ToString()); return Task.FromResult(Json(native));
        });
        var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options()); var result = await adapter.GetResultFileAsync(Completed(), TestContext.Current.CancellationToken);
        Assert.Equal("9007199254740993", result.FileId); Assert.Equal("https://cdn.invalid/video.mp4?signature=fictional%2Fvalue", result.DownloadUrl);
        Assert.Equal(index == 0 ? JsonValueKind.String : JsonValueKind.Number, result.Data.GetProperty("file").GetProperty("file_id").ValueKind);
        Assert.True(JsonElement.DeepEquals(native, result.Data)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>其他文件、其他用途、缺失地址或错误信封不能被公开成当前视频结果。</summary>
    [Theory] [InlineData("file")] [InlineData("id")] [InlineData("purpose")] [InlineData("url")] [InlineData("base")] [InlineData("float-id")]
    public async Task InvalidResultMetadata_FailsExplicitly(string kind)
    {
        var data = JsonNode.Parse(Data().GetProperty("file_results")[0].GetRawText())!.AsObject();
        if (kind == "file") data.Remove("file"); else if (kind == "id") data["file"]!["file_id"] = "123";
        else if (kind == "purpose") data["file"]!["purpose"] = "unrelated-purpose"; else if (kind == "url") data["file"]!.AsObject().Remove("download_url");
        else if (kind == "base") data.Remove("base_resp"); else data["file"]!["file_id"] = 12.5;
        using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(data.ToJsonString()))); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetResultFileAsync(Completed(), TestContext.Current.CancellationToken)); Assert.Equal(1, handler.Calls);
    }
    /// <summary>只有当前 v1 成功视频结果才有此原生调用，不接受运行中任务或任意文件 ID。</summary>
    [Theory] [InlineData("v2")] [InlineData("running")] [InlineData("forged-state")] [InlineData("foreign-task")]
    public async Task InvalidVideoSnapshot_FailsBeforeResultIo(string kind)
    {
        using var handler = new StubHttpHandler((_, _) => throw new InvalidOperationException()); var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options(version: kind == "v2" ? MiniMaxVideoApiVersion.V2 : MiniMaxVideoApiVersion.V1));
        var snapshot = kind switch
        {
            "running" => new VideoTaskSnapshot("hailuo-v1-01", "Processing", VideoTaskState.Running, Query(MiniMaxVideoApiVersion.V1, 2)),
            "forged-state" => new VideoTaskSnapshot("hailuo-v1-01", "Success", VideoTaskState.Succeeded, Query(MiniMaxVideoApiVersion.V1, 2)),
            "foreign-task" => new VideoTaskSnapshot("other-task", "Success", VideoTaskState.Succeeded, Query(MiniMaxVideoApiVersion.V1, 5)), _ => Completed()
        };
        if (kind == "v2") await Assert.ThrowsAsync<NotSupportedException>(() => adapter.GetResultFileAsync(snapshot, TestContext.Current.CancellationToken));
        else if (kind == "foreign-task") await Assert.ThrowsAsync<InvalidDataException>(() => adapter.GetResultFileAsync(snapshot, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAsync<ArgumentException>(() => adapter.GetResultFileAsync(snapshot, TestContext.Current.CancellationToken)); Assert.Equal(0, handler.Calls);
    }
    /// <summary>结果元数据调用的原生 API 错误保留原文，不重试或下载错误结果。</summary>
    [Theory] [InlineData(200)] [InlineData(403)]
    public async Task ResultApiFailure_IsLosslessAndOneShot(int status)
    {
        var native = Data().GetProperty("v1_api_error"); using var handler = new StubHttpHandler((_, _) => Task.FromResult(Json(native, (HttpStatusCode)status)));
        var adapter = new MiniMaxVideoAdapter(new StubHttpClientFactory(handler), Options()); var error = await Assert.ThrowsAsync<VideoUpstreamException>(() => adapter.GetResultFileAsync(Completed(), TestContext.Current.CancellationToken));
        Assert.Equal(native.GetRawText(), System.Text.Encoding.UTF8.GetString(error.ResponseBody.Span)); Assert.Equal(status, (int)error.StatusCode); Assert.Equal(1, handler.Calls);
    }
}
