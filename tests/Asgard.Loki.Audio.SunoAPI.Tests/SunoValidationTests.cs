using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Audio.SunoAPI.Tests;

/// <summary>关联、终态与参数边界，失败时不自动更换目标或改写原生输入。</summary>
public sealed class SunoValidationTests
{
    /// <summary>固定目标在构造时完整验证，错误配置不会创建客户端。</summary>
    [Theory]
    [InlineData("http")] [InlineData("relative")] [InlineData("query")] [InlineData("fragment")] [InlineData("userinfo")] [InlineData("escaped-path")]
    [InlineData("empty-key")] [InlineData("header-key")] [InlineData("nonascii-key")] [InlineData("anonymous-key")] [InlineData("auth")]
    [InlineData("request-limit")] [InlineData("response-limit")] [InlineData("empty-model")] [InlineData("unicode-model")] [InlineData("empty-client")]
    public void InvalidConfiguration_FailsWithoutHttp(string kind)
    {
        using var factory = new SunoTestFactory();
        var root = kind switch { "http" => "http://suno.invalid/", "relative" => "/suno", "query" => "https://suno.invalid/?x=1",
            "fragment" => "https://suno.invalid/#x", "userinfo" => "https://secret@suno.invalid/", "escaped-path" => "https://suno.invalid/a%2Fb/", _ => "https://suno.invalid/" };
        var key = kind switch { "empty-key" => "", "header-key" => "bad\r\nvalue", "nonascii-key" => "凭据", _ => "fixture-key" };
        var auth = kind == "anonymous-key" ? SunoAudioAuthentication.Anonymous : kind == "auth" ? (SunoAudioAuthentication)100 : SunoAudioAuthentication.Bearer;
        var options = SunoFixture.Options(root: root, key: key, auth: auth, model: kind == "empty-model" ? "" : kind == "unicode-model" ? "\ud800" : "fixture-model",
            requestLimit: kind == "request-limit" ? 0 : 1000, responseLimit: kind == "response-limit" ? -1 : 1000, client: kind == "empty-client" ? "" : "fixture");
        Assert.ThrowsAny<ArgumentException>(() => new SunoAudioAdapter(factory, options)); Assert.Equal(0, factory.Creates);
    }

    /// <summary>服务器、原生模型、认证、凭据和读取策略均纳入稳定绑定，摘要不泄露密钥。</summary>
    [Theory] [InlineData("root")] [InlineData("model")] [InlineData("credential")] [InlineData("auth")] [InlineData("request")]
    [InlineData("response")] [InlineData("client")]
    public void CompleteIdentity_ChangesWhenConfigurationChanges(string kind)
    {
        using var factory = new SunoTestFactory(); var original = new SunoAudioAdapter(factory, SunoFixture.Options());
        var changed = new SunoAudioAdapter(factory, SunoFixture.Options(root: kind == "root" ? "https://other.invalid/" : SunoFixture.Options().BaseAddress.AbsoluteUri,
            model: kind == "model" ? "other-model" : "fixture-chirp-version", key: kind == "auth" ? "" : kind == "credential" ? "different-secret" : "fixture-proxy-secret",
            auth: kind == "auth" ? SunoAudioAuthentication.Anonymous : SunoAudioAuthentication.Bearer,
            requestLimit: kind == "request" ? 1000 : 4 * 1024 * 1024, responseLimit: kind == "response" ? 1000 : 16 * 1024 * 1024,
            client: kind == "client" ? "other-client" : "suno-test"));
        Assert.NotEqual(original.BindingIdentity, changed.BindingIdentity); Assert.DoesNotContain("fixture-proxy-secret", original.BindingIdentity);
        Assert.Equal(original.BindingIdentity, new SunoAudioAdapter(factory, SunoFixture.Options()).BindingIdentity); Assert.Equal(0, factory.Creates);
    }

    /// <summary>明确操作与原生字段类型在 HTTP 前验证，不能悄悄覆盖目标模型。</summary>
    [Theory]
    [InlineData("operation")] [InlineData("mv-mismatch")] [InlineData("mv-null")] [InlineData("mv-type")] [InlineData("alias")]
    [InlineData("prompt-type")] [InlineData("instrumental-type")] [InlineData("continue-type")] [InlineData("continue-negative")]
    [InlineData("model-override")] [InlineData("no-music-model")] [InlineData("request-limit")]
    public async Task InvalidRequest_FailsBeforeHttp(string kind)
    {
        using var factory = new SunoTestFactory(); var request = SunoFixture.Request();
        if (kind == "operation") request = request with { Operation = "music" };
        if (kind == "mv-mismatch") request.Parameters["mv"] = "other";
        if (kind == "mv-null") request.Parameters["mv"] = null;
        if (kind == "mv-type") request.Parameters["mv"] = 2;
        if (kind == "alias") request.Parameters["MV"] = "override";
        if (kind == "prompt-type") request.Parameters["prompt"] = 0;
        if (kind == "instrumental-type") request.Parameters["make_instrumental"] = "false";
        if (kind == "continue-type") request.Parameters["continue_at"] = "0";
        if (kind == "continue-negative") request.Parameters["continue_at"] = -1;
        if (kind == "model-override") request.Parameters["model"] = "override";
        var adapter = new SunoAudioAdapter(factory, SunoFixture.Options(model: kind == "no-music-model" ? null : "fixture-chirp-version", requestLimit: kind == "request-limit" ? 2 : 10000));
        if (kind == "no-music-model") await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.SubmitAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, factory.Creates);
    }

    /// <summary>批量缺失不会制造任务，单状态查询明确报告缺失并保留空信封。</summary>
    [Fact]
    public async Task EmptyBatch_IsPreservedAndSingleGetReportsMissing()
    {
        var raw = SunoFixture.Part("batch_empty"); using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(SunoFixture.Json(raw)) };
        var adapter = new SunoAudioAdapter(factory, SunoFixture.Options()); var token = TestContext.Current.CancellationToken;
        var result = await adapter.GetBatchAsync([SunoFixture.Music, SunoFixture.Lyrics], token); Assert.Empty(result.Tasks); Assert.Equal(raw.GetRawText(), result.Data.GetRawText());
        var error = await Assert.ThrowsAsync<SunoAudioTaskMissingException>(() => adapter.GetAsync(SunoFixture.Music, token));
        Assert.Equal(SunoFixture.Music.TaskId, error.TaskId); Assert.Equal(raw.GetRawText(), error.Response.GetRawText()); Assert.Equal(2, factory.Calls);
    }

    /// <summary>错误 ID/操作、嵌套关联、空成功数据和矛盾失败均不能发布成功。</summary>
    [Theory]
    [InlineData("wrong-id")] [InlineData("wrong-action")] [InlineData("duplicate")] [InlineData("no-status")] [InlineData("numeric-status")]
    [InlineData("empty-success")] [InlineData("empty-song-id")] [InlineData("no-media")] [InlineData("duplicate-clip")]
    [InlineData("bad-media")] [InlineData("failure-on-success")] [InlineData("metadata-error")]
    [InlineData("nested-id")] [InlineData("lyrics-no-text")]
    [InlineData("pending-clip")] [InlineData("nested-running-id")]
    public async Task InvalidStatusWindow_IsNeverPublishedAsSuccess(string kind)
    {
        var root = SunoFixture.Object(kind is "nested-id" or "nested-running-id" ? "nested_result" : "batch_partial"); var item = root["data"]![0]!.AsObject();
        switch (kind)
        {
            case "wrong-id": item["task_id"] = "other"; break;
            case "wrong-action": item["action"] = "LYRICS"; break;
            case "duplicate": root["data"]!.AsArray().Add(item.DeepClone()); break;
            case "no-status": item.Remove("status"); break;
            case "numeric-status": item["status"] = 200; break;
            case "empty-success": item["data"] = new JsonArray(); break;
            case "empty-song-id": item["data"]![0]!["id"] = ""; break;
            case "no-media": item["data"]![0]!["audio_url"] = ""; item["data"]![0]!["video_url"] = ""; break;
            case "duplicate-clip": item["data"]![1]!["id"] = item["data"]![0]!["id"]!.DeepClone(); break;
            case "bad-media": item["data"]![0]!["audio_url"] = "file:///secret"; break;
            case "failure-on-success": item["fail_reason"] = "native failure"; break;
            case "metadata-error": item["data"]![0]!["metadata"]!["error_message"] = "native failure"; break;
            case "nested-id": item["data"]!["data"]!["task_id"] = "other"; break;
            case "lyrics-no-text": item["action"] = "LYRICS"; item["data"] = new JsonObject { ["text"] = "" }; break;
            case "pending-clip": item["data"]![0]!["status"] = "streaming"; break;
            case "nested-running-id": item["status"] = "IN_PROGRESS"; item["data"]!["data"]!["task_id"] = "other"; break;
        }
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(SunoFixture.Json(root)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunoAudioAdapter(factory, SunoFixture.Options()).GetAsync(
            kind == "lyrics-no-text" ? new(SunoFixture.Music.TaskId, "LYRICS") : SunoFixture.Music, TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>结果视图中的任务回显必须关联原 ID/操作，即使不解释其状态。</summary>
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RawResult_RejectsWrongNativeEcho(bool wrongAction)
    {
        var raw = SunoFixture.Object("get_gateway_task_result"); raw["data"]![wrongAction ? "action" : "task_id"] = wrongAction ? "LYRICS" : "other";
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(SunoFixture.Json(raw)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunoAudioAdapter(factory, SunoFixture.Options()).GetResultAsync(SunoFixture.Music, TestContext.Current.CancellationToken));
    }

    /// <summary>无效查询不创建客户端，标识不拼进未转义路径。</summary>
    [Theory] [InlineData("")] [InlineData(".")] [InlineData("..")] [InlineData("bad\nvalue")]
    public async Task InvalidQueryId_FailsLocally(string id)
    {
        using var factory = new SunoTestFactory(); var adapter = new SunoAudioAdapter(factory, SunoFixture.Options());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.GetResultAsync(new(id, "MUSIC"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => adapter.GetAsync(new(id, "MUSIC"), TestContext.Current.CancellationToken)); Assert.Equal(0, factory.Creates);
    }

    /// <summary>未知代码类型不能误判成字符串 success，原生 JSON 损坏也不能变成有效结果。</summary>
    [Theory]
    [InlineData("{\"code\":200,\"data\":\"id\"}")]
    [InlineData("{\"code\":\"success\",\"data\":\"id\",\"future\":{\"a\":1,\"a\":2}}")]
    [InlineData("{\"code\":\"success\",\"data\":\"id\",\"future\":\"\\ud800\"}")]
    [InlineData("{\"code\":\"success\",\"data\":{\"task_id\":\"id\"}}")]
    [InlineData("{\"code\":\"success\",\"data\":\"\"}")]
    [InlineData("[]")] [InlineData("{\"code\":\"success\"")]
    public async Task MalformedAcknowledgement_FailsStrictly(string raw)
    {
        using var factory = new SunoTestFactory { OnSend = (_, _) => Task.FromResult(SunoFixture.Json(raw)) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new SunoAudioAdapter(factory, SunoFixture.Options()).SubmitAsync(SunoFixture.Request(), TestContext.Current.CancellationToken));
    }
}
