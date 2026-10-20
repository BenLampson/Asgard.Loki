using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Video.Tests;

/// <summary>可选 HTTP 窗口与真正没有 JSON 的运行中结果，不改变已有 JSON 快照契约。</summary>
public sealed class VideoHttpSnapshotTests
{
    /// <summary>既有构造继续复制原生对象，不要求 HTTP 元数据，也不将空对象视为没有正文。</summary>
    [Fact]
    public void ExistingJsonConstructor_PreservesContract()
    {
        VideoTaskSnapshot snapshot;
        using (var document = JsonDocument.Parse("{\"Future\":9007199254740993}")) snapshot = new("task", null, VideoTaskState.Submitted, document.RootElement);
        Assert.True(snapshot.HasJsonData); Assert.Null(snapshot.HttpResponse); Assert.Equal(9007199254740993L, snapshot.Data.GetProperty("Future").GetInt64());
        var restored = JsonSerializer.Deserialize<VideoTaskSnapshot>(JsonSerializer.Serialize(snapshot))!; Assert.Null(restored.HttpResponse); Assert.True(JsonElement.DeepEquals(snapshot.Data, restored.Data));
        using var empty = JsonDocument.Parse("{}"); Assert.True(new VideoTaskSnapshot("task", null, VideoTaskState.Unknown, empty.RootElement).HasJsonData);
        Assert.Throws<ArgumentException>(() => new VideoTaskSnapshot("task", null, VideoTaskState.Running, default));
    }
    /// <summary>空正文 202 明确没有原生 JSON，任务 ID 仅为调用上下文；序列化也不补造 Data。</summary>
    [Fact]
    public void EmptyAcceptedResponse_PreservesAbsence()
    {
        var response = new VideoTaskHttpResponse(HttpStatusCode.Accepted, ReadOnlyMemory<byte>.Empty);
        var snapshot = new VideoTaskSnapshot("original-request", null, VideoTaskState.Running, default, response);
        Assert.Equal("original-request", snapshot.TaskId); Assert.False(snapshot.HasJsonData); Assert.Equal(JsonValueKind.Undefined, snapshot.Data.ValueKind); Assert.Null(snapshot.NativeStatus); Assert.False(snapshot.IsTerminal);
        Assert.Equal(HttpStatusCode.Accepted, snapshot.HttpResponse!.StatusCode); Assert.Empty(snapshot.HttpResponse.Body.ToArray()); Assert.Null(snapshot.HttpResponse.ContentType);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(snapshot)); Assert.False(serialized.RootElement.TryGetProperty("Data", out _)); Assert.False(serialized.RootElement.GetProperty("HasJsonData").GetBoolean());
        var restored = JsonSerializer.Deserialize<VideoTaskSnapshot>(serialized.RootElement.GetRawText())!; Assert.False(restored.HasJsonData); Assert.Equal(HttpStatusCode.Accepted, restored.HttpResponse!.StatusCode); Assert.Empty(restored.HttpResponse.Body.ToArray());
    }
    /// <summary>HTTP 正文从输入和每次输出均独立复制，不泄露可修改的共享数组。</summary>
    [Fact]
    public void HttpBody_IsIndependentOnInputAndOutput()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"Keep\":false}"); var response = new VideoTaskHttpResponse(HttpStatusCode.OK, bytes, "application/json; charset=utf-8"); bytes[0] = 0;
        Assert.True(MemoryMarshal.TryGetArray(response.Body, out var exposed)); exposed.Array![exposed.Offset] = 0;
        Assert.Equal("{\"Keep\":false}", Encoding.UTF8.GetString(response.Body.Span)); Assert.Equal("application/json; charset=utf-8", response.ContentType);
        using var document = JsonDocument.Parse(response.Body); var snapshot = new VideoTaskSnapshot("task", "pending", VideoTaskState.Running, document.RootElement, response);
        Assert.True(snapshot.HasJsonData); Assert.False(snapshot.Data.GetProperty("Keep").GetBoolean()); Assert.Same(response, snapshot.HttpResponse);
    }
    /// <summary>没有 JSON 不是任意成功、失败或未知结果的替代表示。</summary>
    [Theory] [InlineData(VideoTaskState.Unknown)] [InlineData(VideoTaskState.Submitted)] [InlineData(VideoTaskState.Queued)]
    [InlineData(VideoTaskState.Succeeded)] [InlineData(VideoTaskState.Failed)] [InlineData(VideoTaskState.Cancelled)] [InlineData(VideoTaskState.Expired)]
    public void AbsentJson_RequiresRunningState(VideoTaskState state)
        => Assert.Throws<ArgumentException>(() => new VideoTaskSnapshot("task", null, state, default, new(HttpStatusCode.Accepted, ReadOnlyMemory<byte>.Empty)));
    /// <summary>没有 JSON 必须对应真正空的 202，其他状态、正文、状态文本或 JSON null 均不满足条件。</summary>
    [Theory] [InlineData("http")] [InlineData("body")] [InlineData("whitespace")] [InlineData("native-status")] [InlineData("json-null")]
    public void AbsentJson_RequiresExactNativeAbsence(string kind)
    {
        using var jsonNull = JsonDocument.Parse("null"); var data = kind == "json-null" ? jsonNull.RootElement : default;
        var response = new VideoTaskHttpResponse(kind == "http" ? HttpStatusCode.OK : HttpStatusCode.Accepted,
            kind == "body" ? "{}"u8.ToArray() : kind == "whitespace" ? " "u8.ToArray() : ReadOnlyMemory<byte>.Empty);
        Assert.Throws<ArgumentException>(() => new VideoTaskSnapshot("task", kind == "native-status" ? "pending" : null, VideoTaskState.Running, data, response));
    }
    /// <summary>无效 HTTP 状态和缺失的显式窗口不能被接受。</summary>
    [Fact]
    public void InvalidHttpMetadata_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoTaskHttpResponse((HttpStatusCode)0, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoTaskHttpResponse((HttpStatusCode)600, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentException>(() => new VideoTaskSnapshot("task", null, VideoTaskState.Running, default, null));
    }
}
