using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.Google.Tests;

/// <summary>独立固定原生样本及离线目标，所有媒体与凭据均为虚构。</summary>
internal static class VeoFixture
{
    internal const string Model = "veo-3.1-generate-preview";
    internal const string Operation = "models/" + Model + "/operations/fixture-01";
    internal static readonly Uri ContentUri = new("https://veo.invalid/v1beta/files/fixture-a:download?alt=media");
    /// <summary>读取独立提交的原生 REST 样本。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(VeoFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Google.Tests.Fixtures.veo.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>原生正文不注入实际模型或转换媒体字段。</summary>
    internal static VideoTaskRequest Request(int index = 0) => new() { Model = "video", Parameters = JsonNode.Parse(Data().GetProperty("submissions")[index].GetRawText())!.AsObject() };
    /// <summary>虚构完整目标，允许测试只改变一项绑定策略。</summary>
    internal static GoogleVideoOptions Options(string model = Model, string root = "https://veo.invalid/v1beta/", string key = "fixture-veo-key", string client = "veo-test",
        int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024, long contentLimit = 512 * 1024 * 1024, int redirects = 3)
        => new() { Id = "veo-target", Model = model, BaseAddress = new(root), ApiKey = key, HttpClientName = client, MaximumRequestBytes = requestLimit,
            MaximumResponseBytes = responseLimit, MaximumContentBytes = contentLimit, MaximumContentRedirects = redirects };
    /// <summary>通过公开能力入口保持原始任务及完整目标引用。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, GoogleVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new GoogleVideoAdapter(factory, options ?? Options())] }] }).Build();
    /// <summary>每次创建独立响应，调用可以完整拥有并释放。</summary>
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => Json(value.GetRawText(), status);
    /// <summary>允许构造损坏原生协议，不接触网络。</summary>
    internal static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    /// <summary>固定顺序的运行、错误、过滤与成功结果。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
}
