using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.Midjourney.Tests;

/// <summary>独立代理视频样本及虚构目标，只通过公开 SDK 入口验证。</summary>
internal static class MidjourneyFixture
{
    /// <summary>读取先于适配器提交的固定原生 JSON。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(MidjourneyFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Midjourney.Tests.Fixtures.midjourney.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>原生 videoType 移交目标配置，保留父任务引用与零索引。</summary>
    internal static VideoTaskRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Case(index).GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("videoType");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>完整原始生成或延长请求。</summary>
    internal static JsonElement Case(int index) => Data().GetProperty("submissions")[index];
    /// <summary>原始提交码与已有任务状态窗口。</summary>
    internal static JsonElement Ack(int index = 0) => Data().GetProperty("acknowledgements")[index];
    /// <summary>完整原生生命周期窗口。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
    /// <summary>独立配置服务、实际视频类型和匿名或密钥认证。</summary>
    internal static MidjourneyVideoOptions Options(string videoType = "vid_1.1_i2v_480", string root = "https://mj.invalid/mj/", string? secret = "fixture-mj-secret",
        string client = "mj-video-test", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = "mj-video-target", VideoType = videoType, BaseAddress = new(root), ApiSecret = secret, HttpClientName = client,
            MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>经由独立 Video 能力保留原目标任务引用。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, MidjourneyVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new MidjourneyVideoAdapter(factory, options ?? Options())] }] }).Build();
    /// <summary>每个响应单独分配并交给被测调用释放。</summary>
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => Json(data.GetRawText(), status);
    /// <summary>构造有效或损坏的原生响应。</summary>
    internal static HttpResponseMessage Json(string data, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
}
