using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.SunoAPI.Tests;

/// <summary>完整独立请求/响应样本验证原生协议，不接触真实代理或媒体。</summary>
public sealed class SunoWireTests
{
    /// <summary>三种原生输入保持字段、未知值与续写关联，未使用旧模型/参数默认值。</summary>
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Submit_MatchesIndependentWireAndOriginalAcknowledgement(int index)
    {
        var request = SunoFixture.Request(index); var expected = SunoFixture.Part("requests")[index].GetProperty("body");
        var response = SunoFixture.Part("submit").GetProperty(index == 2 ? "lyrics" : "music");
        using var factory = new SunoTestFactory { OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("https://suno-proxy.example.invalid/prefix/suno/submit/" + request.Operation, message.RequestUri!.AbsoluteUri);
            Assert.Equal(SunoFixture.Part("target").GetProperty("authorization").GetString(), message.Headers.Authorization!.ToString());
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            var bytes = await message.Content.ReadAsByteArrayAsync(token); using var document = JsonDocument.Parse(bytes);
            Assert.True(JsonElement.DeepEquals(expected, document.RootElement)); Assert.Contains("9007199254740993", Encoding.UTF8.GetString(bytes));
            Assert.False(document.RootElement.TryGetProperty("model", out _));
            return SunoFixture.Json(response);
        } };
        await using var client = SunoFixture.Build(factory); var result = await client.Audio.Tasks.SubmitAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(response.GetRawText(), result.Snapshot.Data.GetRawText()); Assert.Null(result.Snapshot.NativeStatus);
        Assert.Equal(AudioTaskState.Submitted, result.Snapshot.State); Assert.False(result.Snapshot.IsTerminal);
        Assert.Equal(response.GetProperty("data").GetString(), result.Reference.TaskId); Assert.Equal(request.Operation, result.Reference.Operation);
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.ClientDisposals); Assert.Equal("suno-test", factory.LastClientName); Assert.False(factory.Disposed);
    }

    /// <summary>音乐 mv 仅按完整目标填充；歌词不注入任何音乐/器乐默认值。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OmittedDefaults_AreNotInvented(bool lyrics)
    {
        var request = SunoFixture.Request(lyrics ? 2 : 0); request.Parameters.Remove("mv"); request.Parameters.Remove("make_instrumental");
        using var factory = new SunoTestFactory { OnSend = async (message, token) =>
        {
            using var body = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.False(body.RootElement.TryGetProperty("make_instrumental", out _));
            if (lyrics) Assert.False(body.RootElement.TryGetProperty("mv", out _));
            else Assert.Equal("fixture-chirp-version", body.RootElement.GetProperty("mv").GetString());
            return SunoFixture.Json(SunoFixture.Part("submit").GetProperty(lyrics ? "lyrics" : "music"));
        } };
        var adapter = new SunoAudioAdapter(factory, SunoFixture.Options(model: lyrics ? null : "fixture-chirp-version"));
        await adapter.SubmitAsync(request, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }

    /// <summary>原生批量是一次 POST，混合音乐/歌词可保留逆序结果及所有元数据。</summary>
    [Fact]
    public async Task Batch_UsesNativeIdsAndPreservesFullMixedResult()
    {
        using var factory = new SunoTestFactory { OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("/prefix/suno/fetch", message.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.True(JsonElement.DeepEquals(SunoFixture.Part("batch_request"), document.RootElement));
            return SunoFixture.Json(SunoFixture.Part("batch_result"));
        } };
        var result = await new SunoAudioAdapter(factory, SunoFixture.Options()).GetBatchAsync([SunoFixture.Music, SunoFixture.Lyrics], TestContext.Current.CancellationToken);
        Assert.Equal(SunoFixture.Part("batch_result").GetRawText(), result.Data.GetRawText());
        Assert.Equal(new[] { "task-lyrics-02", "task-music-01" }, result.Tasks.Select(task => task.TaskId));
        Assert.All(result.Tasks, task => { Assert.Equal(AudioTaskState.Succeeded, task.State); Assert.True(task.IsTerminal); });
        var songs = result.Tasks[1].Data.GetProperty("data"); Assert.Equal(2, songs.GetArrayLength());
        Assert.Equal("original-clip-1", songs[0].GetProperty("id").GetString()); Assert.Equal("original-clip-2", songs[1].GetProperty("id").GetString());
        Assert.Equal("https://media.example.invalid/audio%2Fone.mp3?signature=fixture%2B1", songs[0].GetProperty("audio_url").GetString());
        Assert.Equal("9007199254740993", songs[0].GetProperty("play_count").GetRawText()); Assert.Equal(1, factory.Calls);
    }

    /// <summary>单次状态查询使用批量状态入口，不误用没有状态的 GET 结果。</summary>
    [Fact]
    public async Task SingleStatus_UsesOneItemNativeBatch()
    {
        using var factory = new SunoTestFactory { OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal("/prefix/suno/fetch", message.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await message.Content!.ReadAsByteArrayAsync(token));
            Assert.Equal(new[] { SunoFixture.Music.TaskId }, document.RootElement.GetProperty("ids").EnumerateArray().Select(item => item.GetString()));
            return SunoFixture.Json(SunoFixture.Part("batch_partial"));
        } };
        var result = await new SunoAudioAdapter(factory, SunoFixture.Options()).GetAsync(SunoFixture.Music, TestContext.Current.CancellationToken);
        Assert.Equal(AudioTaskState.Succeeded, result.State); Assert.Equal(SunoFixture.Music.TaskId, result.TaskId); Assert.Equal(1, factory.Calls);
    }

    /// <summary>作者原生数据结果和兼容任务包装均保留完整信封，不补造状态。</summary>
    [Theory] [InlineData("get_music_result", "MUSIC")] [InlineData("get_lyrics_result", "LYRICS")] [InlineData("get_gateway_task_result", "MUSIC")]
    public async Task ResultGet_RetainsIndependentNativeData(string name, string operation)
    {
        var query = operation == "MUSIC" ? SunoFixture.Music : SunoFixture.Lyrics;
        using var factory = new SunoTestFactory { DefaultAuthorization = new("Bearer", "factory-default-must-not-leak"), OnSend = (message, _) =>
        {
            Assert.Equal(HttpMethod.Get, message.Method); Assert.Equal("/prefix/suno/fetch/" + query.TaskId, message.RequestUri!.AbsolutePath); Assert.Null(message.Content);
            return Task.FromResult(SunoFixture.Json(SunoFixture.Part(name)));
        } };
        var result = await new SunoAudioAdapter(factory, SunoFixture.Options()).GetResultAsync(query, TestContext.Current.CancellationToken);
        Assert.Equal(SunoFixture.Part(name).GetRawText(), result.GetRawText()); Assert.Equal(1, factory.Calls);
    }

    /// <summary>各原生状态完整保留；未知状态和局部音频不是完成。</summary>
    [Theory]
    [InlineData(0, AudioTaskState.Submitted)] [InlineData(1, AudioTaskState.Submitted)] [InlineData(2, AudioTaskState.Queued)]
    [InlineData(3, AudioTaskState.Running)] [InlineData(4, AudioTaskState.Unknown)] [InlineData(5, AudioTaskState.Unknown)]
    [InlineData(6, AudioTaskState.Unknown)] [InlineData(7, AudioTaskState.Failed)]
    public async Task NativeLifecycle_DoesNotGuessTerminalFromData(int index, AudioTaskState state)
    {
        var raw = SunoFixture.Part("state_windows")[index];
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(SunoFixture.Json(raw)) };
        var result = await new SunoAudioAdapter(factory, SunoFixture.Options()).GetAsync(SunoFixture.Music, TestContext.Current.CancellationToken);
        Assert.Equal(state, result.State); Assert.Equal(state == AudioTaskState.Failed, result.IsTerminal);
        Assert.Equal(raw.GetProperty("data")[0].GetRawText(), result.Data.GetRawText());
    }

    /// <summary>旧嵌套数据可验证结果，但不能丢掉其原包装或关联任务。</summary>
    [Fact]
    public async Task NestedResult_KeepsOriginalCarrier()
    {
        var raw = SunoFixture.Part("nested_result");
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(SunoFixture.Json(raw)) };
        var result = await new SunoAudioAdapter(factory, SunoFixture.Options()).GetAsync(SunoFixture.Music, TestContext.Current.CancellationToken);
        Assert.Equal(AudioTaskState.Succeeded, result.State); Assert.Equal(raw.GetProperty("data")[0].GetRawText(), result.Data.GetRawText());
    }

    /// <summary>匿名配置不会借用工厂默认凭据；请求本身没有认证头。</summary>
    [Fact]
    public async Task ExplicitAnonymous_SendsNoAuthorization()
    {
        using var factory = new SunoTestFactory { DefaultAuthorization = new("Bearer", "factory-default-must-not-leak"), OnSend = (message, _) =>
        { Assert.Null(message.Headers.Authorization); return Task.FromResult(SunoFixture.Json(SunoFixture.Part("submit").GetProperty("lyrics"))); } };
        var adapter = new SunoAudioAdapter(factory, SunoFixture.Options(model: null, key: "", auth: SunoAudioAuthentication.Anonymous));
        await adapter.SubmitAsync(SunoFixture.Request(2), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
}
