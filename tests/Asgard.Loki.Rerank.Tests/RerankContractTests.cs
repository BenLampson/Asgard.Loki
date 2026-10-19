using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Rerank.Tests;

/// <summary>验证注册错误、原生 JSON 边界和结果快照，不假设供应商共用排序结果结构。</summary>
public sealed class RerankContractTests
{
    /// <summary>配置错误必须在 AddRerank 时暴露，不能等首次网络调用才失败。</summary>
    [Theory]
    [InlineData("options")] [InlineData("routes")] [InlineData("empty")] [InlineData("route")]
    [InlineData("model")] [InlineData("targets")] [InlineData("no-targets")] [InlineData("target")]
    [InlineData("identity")] [InlineData("duplicate")]
    public void InvalidConfiguration_FailsAtRegistration(string kind)
    {
        using var adapter = new StubRerankAdapter(kind == "identity" ? " " : "native");
        var route = new RerankRoute { Model = "public", Targets = [adapter] };
        var options = new RerankOptions { Routes = [route] };
        switch (kind)
        {
            case "options": options = null!; break;
            case "routes": options = options with { Routes = null! }; break;
            case "empty": options = options with { Routes = [] }; break;
            case "route": options = options with { Routes = [null!] }; break;
            case "model": options = options with { Routes = [route with { Model = " " }] }; break;
            case "targets": options = options with { Routes = [route with { Targets = null! }] }; break;
            case "no-targets": options = options with { Routes = [route with { Targets = [] }] }; break;
            case "target": options = options with { Routes = [route with { Targets = [null!] }] }; break;
            case "duplicate": options = options with { Routes = [route, route] }; break;
        }
        Assert.ThrowsAny<ArgumentException>(() => new LokiClientBuilder().AddRerank(options)); Assert.Equal(0, adapter.Calls);
    }

    /// <summary>模块通过普通注册机制创建，重复或缺失注册明确失败。</summary>
    [Fact]
    public async Task ModuleRegistration_UsesExistingClientContract()
    {
        using var adapter = new StubRerankAdapter("native");
        var builder = new LokiClientBuilder(); await using var unregistered = builder.Build();
        Assert.Throws<InvalidOperationException>(() => unregistered.Rerank);
        var options = new RerankOptions { Routes = [new() { Model = "public", Targets = [adapter] }] };
        builder.AddRerank(options);
        Assert.Throws<InvalidOperationException>(() => builder.AddRerank(options));
        Assert.Equal(0, adapter.Calls);
    }

    /// <summary>核心不解释原生排名/分数，不按索引排序或为未知用量填零，未来方言也保留原文。</summary>
    [Theory]
    [InlineData("""{"results":[{"index":2,"relevance_score":0.9},{"index":0,"relevance_score":0}],"meta":{"future":null},"future":false}""")]
    [InlineData("""{"results":[{"index":1,"relevance_score":0.25,"document":{"text":"原文😀","future":false}}],"usage":{"total_tokens":null}}""")]
    [InlineData("""{"ranking":[{"doc_ref":"opaque","score":null}],"future":null}""")]
    public void Result_PreservesIndependentNativeJson(string json)
    {
        var source = JsonDocument.Parse(json); var result = new RerankResult(source.RootElement, new("public", "actual", "native"));
        source.Dispose(); using var expected = JsonDocument.Parse(json);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, result.Data)); Assert.Equal("actual", result.Metadata.UpstreamModel);
        Assert.False(result.Data.TryGetProperty("invented_tokens", out _));
    }

    /// <summary>公共结果必须为原生对象，不把空值或非对象伪装成空排序结果集合。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("1")] [InlineData("\"text\"")]
    public void Result_RejectsNonObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => new RerankResult(document.RootElement, new("public", "actual", "native")));
    }

    /// <summary>完整原生参数与公共 model 并列，不向原始查询或文档增加包装层。</summary>
    [Fact]
    public void Request_SerializesNativeFieldsWithoutReinterpretation()
    {
        var request = new RerankRequest { Model = "public", Parameters = new()
        {
            ["query"] = JsonSerializer.SerializeToElement("rank😀"), ["documents"] = JsonSerializer.SerializeToElement(new[] { "one", "two" }),
            ["top_n"] = JsonSerializer.SerializeToElement(1), ["return_documents"] = JsonSerializer.SerializeToElement(false),
            ["future"] = JsonSerializer.SerializeToElement(new { enabled = false, value = (string?)null })
        } };
        using var expected = JsonDocument.Parse("""{"model":"public","query":"rank😀","documents":["one","two"],"top_n":1,"return_documents":false,"future":{"enabled":false,"value":null}}""");
        Assert.True(JsonElement.DeepEquals(expected.RootElement, JsonSerializer.SerializeToElement(request)));
    }

    /// <summary>JSON 字段严格区分大小写，不继承调用者字典的忽略大小写语义来误删扩展字段。</summary>
    [Fact]
    public async Task NativeKeys_KeepExactCaseDespiteDictionaryComparer()
    {
        using var adapter = new StubRerankAdapter("native", (request, _) => Task.FromResult(
            new RerankResult(JsonSerializer.SerializeToElement(request), new(request.Model, "actual", "native"))));
        await using var client = new LokiClientBuilder().AddRerank(new() { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
        var result = await client.Rerank.RerankAsync(new() { Model = "public", Parameters = new(StringComparer.OrdinalIgnoreCase)
            { ["MODEL"] = JsonSerializer.SerializeToElement("extension") } }, TestContext.Current.CancellationToken);
        Assert.Equal("public", result.Data.GetProperty("model").GetString()); Assert.Equal("extension", result.Data.GetProperty("MODEL").GetString());
    }
}
