using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.MiniMax.Tests;

/// <summary>独立固定原生样本，通过公开 SDK API 验证，不读取真实凭据或连接网络。</summary>
internal static class HailuoFixture
{
    /// <summary>读取独立提交的完整原生请求、结果和错误样本。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(HailuoFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.MiniMax.Tests.Fixtures.hailuo.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>模型由完整目标固定，其余原生字段完整保留。</summary>
    internal static VideoTaskRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Data().GetProperty("submissions")[index].GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("model");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>允许独立修改一个目标绑定条件的虚构配置。</summary>
    internal static MiniMaxVideoOptions Options(MiniMaxVideoApiVersion version = MiniMaxVideoApiVersion.V1, string model = "configured-model", string root = "https://hailuo.invalid/",
        string key = "fixture-hailuo-key", string client = "hailuo-test", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = "hailuo-target", ApiVersion = version, Model = model, BaseAddress = new(root), ApiKey = key, HttpClientName = client,
            MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>通过公共 Video 能力保持原始任务和持久化完整目标引用。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, MiniMaxVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new MiniMaxVideoAdapter(factory, options ?? Options())] }] }).Build();
    /// <summary>每次创建独立响应，生命周期交给被测调用。</summary>
    internal static HttpResponseMessage Json(JsonElement value, HttpStatusCode status = HttpStatusCode.OK) => Json(value.GetRawText(), status);
    /// <summary>允许构造协议损坏正文。</summary>
    internal static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    /// <summary>显式选择原生版本，不依据模型名称。</summary>
    internal static JsonElement Query(MiniMaxVideoApiVersion version, int index) => Data().GetProperty(version == MiniMaxVideoApiVersion.V1 ? "v1_queries" : "v2_queries")[index];
    /// <summary>原生成功快照用于单独验证结果文件入口，不模拟上游真实生成。</summary>
    internal static VideoTaskSnapshot Completed() => new("hailuo-v1-01", "Success", VideoTaskState.Succeeded, Query(MiniMaxVideoApiVersion.V1, 5));
}
