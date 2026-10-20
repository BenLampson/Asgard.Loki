using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Video.Tests;

/// <summary>验证原生素材引用、任务身份、状态和结果的完整窗口。</summary>
public sealed class VideoTranscriptTests
{
    /// <summary>状态、失败和带签名结果保留完整原生语义，不能把 HTTP 成功当任务成功。</summary>
    [Fact]
    public void ArkFixture_HasIndependentNativeLifecycleAndAssetReferences()
    {
        using var stream = typeof(VideoTranscriptTests).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Tests.Fixtures.ark.json")!;
        using var document = JsonDocument.Parse(stream); var root = document.RootElement;
        Assert.Contains("not live", root.GetProperty("provenance").GetProperty("kind").GetString());
        Assert.Equal("asset://asset_figure", root.GetProperty("wire_request").GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        var queries = root.GetProperty("queries").EnumerateArray().ToArray();
        Assert.Equal(["queued","running","failed","expired","cancelled","future_status","succeeded"], queries.Select(e => e.GetProperty("status").GetString()));
        Assert.All(queries, e => Assert.Equal(root.GetProperty("submitted").GetProperty("id").GetString(), e.GetProperty("id").GetString()));
        Assert.Equal("InputRejected", queries[2].GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("signature=opaque", queries[^1].GetProperty("content").GetProperty("video_url").GetString());
    }
}
