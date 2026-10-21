using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.Kling.Tests;

/// <summary>独立可灵原生样本和虚构完整目标，仅通过公开 SDK 入口验证。</summary>
internal static class KlingFixture
{
    /// <summary>读取在原生实现前提交的固定样本。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(KlingFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Kling.Tests.Fixtures.kling.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>实际 model_name 由完整目标提供，其他原生字段不转换。</summary>
    internal static VideoTaskRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Data().GetProperty("submissions")[index].GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("model_name");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>可单独修改一项目标身份的独立配置。</summary>
    internal static KlingVideoOptions Options(KlingVideoOperation operation = KlingVideoOperation.TextToVideo, KlingVideoAuthentication authentication = KlingVideoAuthentication.AccessKeyJwt,
        string model = "kling-v2-6", string root = "https://kling.invalid/", string apiKey = "fixture-api-key", string access = "fixture-access", string secret = "fixture-secret-only",
        string client = "kling-test", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = "kling-target", Operation = operation, Authentication = authentication, Model = model, BaseAddress = new(root), HttpClientName = client,
            ApiKey = authentication == KlingVideoAuthentication.ApiKey ? apiKey : "", AccessKeyId = authentication == KlingVideoAuthentication.AccessKeyJwt ? access : "",
            SecretAccessKey = authentication == KlingVideoAuthentication.AccessKeyJwt ? secret : "", MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>经由公开模块注册测试完整任务与持久化引用。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, KlingVideoOptions? options = null, KlingTestClock? clock = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new KlingVideoAdapter(factory, options ?? Options(), clock ?? new())] }] }).Build();
    /// <summary>每次响应独立分配，允许被测调用拥有并释放。</summary>
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => Json(data.GetRawText(), status);
    /// <summary>支持构造损坏原生协议。</summary>
    internal static HttpResponseMessage Json(string data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(data, Encoding.UTF8, "application/json") };
    /// <summary>固定原生状态窗口。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
}
