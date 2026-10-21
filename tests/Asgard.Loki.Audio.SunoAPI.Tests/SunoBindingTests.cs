using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.SunoAPI.Tests;

/// <summary>公开 SDK 入口与原生供应商共同验证绑定、遗漏和结果关联。</summary>
public sealed class SunoBindingTests
{
    /// <summary>同一完整目标混合操作时不混用摘要，批量按请求关联而不重写原生数组。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task MixedTasks_OrderReferencesAndRetainNativeBatch(bool partial)
    {
        using var factory = new SunoTestFactory { OnSend = (message, _) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            return Task.FromResult(SunoFixture.Json(path.EndsWith("MUSIC", StringComparison.Ordinal) ? SunoFixture.Part("submit").GetProperty("music")
                : path.EndsWith("LYRICS", StringComparison.Ordinal) ? SunoFixture.Part("submit").GetProperty("lyrics")
                : SunoFixture.Part(partial ? "batch_partial" : "batch_result")));
        } };
        await using var client = SunoFixture.Build(factory); var token = TestContext.Current.CancellationToken;
        var music = (await client.Audio.Tasks.SubmitAsync(SunoFixture.Request(), token)).Reference;
        var lyrics = (await client.Audio.Tasks.SubmitAsync(SunoFixture.Request(2), token)).Reference;
        Assert.NotEqual(music.Fingerprint, lyrics.Fingerprint); Assert.Equal(music.TargetId, lyrics.TargetId);
        var result = await client.Audio.Tasks.GetBatchAsync([music, lyrics], token);
        Assert.Equal(partial ? new[] { music } : new[] { music, lyrics }, result.Tasks.Select(task => task.Reference));
        Assert.Equal(partial ? new[] { lyrics } : [], result.MissingReferences);
        Assert.Equal(SunoFixture.Part(partial ? "batch_partial" : "batch_result").GetRawText(), result.Data.GetRawText());
        Assert.Equal(3, factory.Calls); Assert.Equal(3, factory.ClientDisposals); Assert.False(factory.Disposed);
    }

    /// <summary>持久化引用可在等价新客户端恢复，凭据改变时不能默默查询其他配置。</summary>
    [Fact]
    public async Task RestoredReference_RequiresSameNativeTarget()
    {
        using var factory = new SunoTestFactory(); await using var client = SunoFixture.Build(factory); var token = TestContext.Current.CancellationToken;
        var task = await client.Audio.Tasks.SubmitAsync(SunoFixture.Request(), token);
        var saved = JsonSerializer.Serialize(task.Reference); Assert.DoesNotContain("fixture-proxy-secret", saved);
        var restored = JsonSerializer.Deserialize<AudioTaskReference>(saved)!;
        factory.OnSend = (_, _) => Task.FromResult(SunoFixture.Json(SunoFixture.Part("batch_partial")));
        await using var next = SunoFixture.Build(factory); Assert.Equal(restored, (await next.Audio.Tasks.GetAsync(restored, token)).Reference);
        await using var changed = SunoFixture.Build(factory, SunoFixture.Options(key: "changed-fixture-key"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.Audio.Tasks.GetAsync(restored, token)); Assert.Equal(2, factory.Calls);
    }

    /// <summary>原生数据无状态且为空时，结果视图保留原文，不制造终态。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("{}")]
    public async Task RawResult_LeavesEmptyNativeDataUninterpreted(string data)
    {
        using var factory = new SunoTestFactory(); await using var client = SunoFixture.Build(factory); var token = TestContext.Current.CancellationToken;
        var reference = (await client.Audio.Tasks.SubmitAsync(SunoFixture.Request(), token)).Reference;
        var raw = "{ \"code\":\"success\", \"data\":" + data + ",\"future\":null }";
        factory.OnSend = (_, _) => Task.FromResult(SunoFixture.Json(raw));
        var result = await client.Audio.Tasks.GetResultAsync(reference, token); Assert.Equal(raw, result.Data.GetRawText());
        Assert.Equal(reference, result.Reference); Assert.False(result.Data.TryGetProperty("status", out _)); Assert.Equal(2, factory.Calls);
    }

    /// <summary>GET 任务 ID 是一个转义路径段，特殊字符不会添加查询或新的路径。</summary>
    [Fact]
    public async Task RawResult_EscapesOriginalTaskIdOnce()
    {
        const string id = "original/a?b=#%2F";
        using var factory = new SunoTestFactory { OnSend = (message, _) =>
        {
            Assert.Equal("https://suno-proxy.example.invalid/prefix/suno/fetch/original%2Fa%3Fb%3D%23%252F", message.RequestUri!.AbsoluteUri);
            Assert.Equal("", message.RequestUri.Query); Assert.Equal("", message.RequestUri.Fragment);
            return Task.FromResult(SunoFixture.Json("{\"code\":\"success\",\"data\":null}"));
        } };
        await new SunoAudioAdapter(factory, SunoFixture.Options()).GetResultAsync(new(id, "MUSIC"), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }

    /// <summary>原生音乐单对象和歌词数组不被重新包装或压平。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeResultContainer_RetainsObjectOrArray(bool lyrics)
    {
        var root = SunoFixture.Object("batch_result"); var item = root["data"]![lyrics ? 0 : 1]!.DeepClone();
        item!["data"] = lyrics ? new JsonArray(item["data"]!.DeepClone()) : item["data"]![0]!.DeepClone(); root["data"] = new JsonArray(item);
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(SunoFixture.Json(root)) };
        var result = await new SunoAudioAdapter(factory, SunoFixture.Options()).GetAsync(lyrics ? SunoFixture.Lyrics : SunoFixture.Music, TestContext.Current.CancellationToken);
        Assert.Equal(AudioTaskState.Succeeded, result.State); Assert.True(JsonNode.DeepEquals(item, JsonNode.Parse(result.Data.GetRawText())));
    }
}
