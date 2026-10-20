using System.Text.Json;
using System.Text.Json.Nodes;
using Asgard.Loki.Abstractions;

namespace Asgard.Loki.Video.Volcengine.Tests;

/// <summary>独立 Visual 原生样本和标准库签名向量，不运行真实生成请求。</summary>
internal static class JimengFixture
{
    /// <summary>读取实现前固定的完整原生协议样本。</summary>
    internal static JsonElement Data()
    {
        using var stream = typeof(JimengFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Video.Volcengine.Tests.Fixtures.jimeng.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.Clone();
    }
    /// <summary>固定服务标识由目标配置提供，其余原生字段原样传递。</summary>
    internal static VideoTaskRequest Request(int index = 0)
    {
        var parameters = JsonNode.Parse(Data().GetProperty("submissions")[index].GetProperty("wire_request").GetRawText())!.AsObject(); parameters.Remove("req_key");
        return new() { Model = "video", Parameters = parameters };
    }
    /// <summary>虚构完整目标，可只改变一项身份或资源策略。</summary>
    internal static JimengVideoOptions Options(string requestKey = "fixture_req_key", string host = "jimeng.invalid", string access = "fixture-access", string secret = "fixture-secret-only",
        string? token = null, string client = "jimeng-test", int requestLimit = 64 * 1024 * 1024, int responseLimit = 16 * 1024 * 1024)
        => new() { Id = "jimeng-target", RequestKey = requestKey, BaseAddress = new("https://" + host + "/"), AccessKeyId = access, SecretAccessKey = secret, SecurityToken = token,
            HttpClientName = client, MaximumRequestBytes = requestLimit, MaximumResponseBytes = responseLimit };
    /// <summary>经由公共能力入口验证持久化完整目标绑定。</summary>
    internal static ILokiClient Build(IHttpClientFactory factory, JimengVideoOptions? options = null)
        => new LokiClientBuilder().AddVideo(new() { Routes = [new() { Model = "video", Targets = [new JimengVideoAdapter(factory, options ?? Options(), new AssetTestClock())] }] }).Build();
    /// <summary>原生查询不保证回传任务标识，完整 JSON 不补造字段。</summary>
    internal static JsonElement Query(int index) => Data().GetProperty("queries")[index];
}
