using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Embedding.Tests;

/// <summary>验证注册错误、原生 JSON 边界和结果快照，不假设供应商共用向量结构。</summary>
public sealed class EmbeddingContractTests
{
    /// <summary>配置错误必须在 AddEmbedding 时暴露，不能等首次网络调用才失败。</summary>
    [Theory]
    [InlineData("options")] [InlineData("routes")] [InlineData("empty")] [InlineData("route")]
    [InlineData("model")] [InlineData("targets")] [InlineData("no-targets")] [InlineData("target")]
    [InlineData("identity")] [InlineData("duplicate")]
    public void InvalidConfiguration_FailsAtRegistration(string kind)
    {
        using var adapter = new StubEmbeddingAdapter(kind == "identity" ? " " : "native");
        var route = new EmbeddingRoute { Model = "public", Targets = [adapter] };
        var options = new EmbeddingOptions { Routes = [route] };
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
        Assert.ThrowsAny<ArgumentException>(() => new LokiClientBuilder().AddEmbedding(options)); Assert.Equal(0, adapter.Calls);
    }

    /// <summary>模块通过普通注册机制创建，重复或缺失注册明确失败。</summary>
    [Fact]
    public async Task ModuleRegistration_UsesExistingClientContract()
    {
        using var adapter = new StubEmbeddingAdapter("native");
        var builder = new LokiClientBuilder(); await using var unregistered = builder.Build();
        Assert.Throws<InvalidOperationException>(() => unregistered.Embedding);
        var options = new EmbeddingOptions { Routes = [new() { Model = "public", Targets = [adapter] }] };
        builder.AddEmbedding(options);
        Assert.Throws<InvalidOperationException>(() => builder.AddEmbedding(options));
        Assert.Equal(0, adapter.Calls);
    }

    /// <summary>结果不要求 OpenAI 外壳，也不重排索引、解码 Base64 或为未知用量填零。</summary>
    [Theory]
    [InlineData("""{"data":[{"index":1,"embedding":"AACAPw=="},{"index":0,"embedding":[0.1,-0.2]}],"usage":{"future":null},"future":false}""")]
    [InlineData("""{"embedding":{"values":[0.1,-0.2]},"future":{"opaque":"kept"}}""")]
    [InlineData("""{"embeddings":[{"values":[1]},{"values":[2]}],"future":null}""")]
    public void Result_PreservesIndependentNativeJson(string json)
    {
        var source = JsonDocument.Parse(json); var result = new EmbeddingResult(source.RootElement, new("public", "actual", "native"));
        source.Dispose(); using var expected = JsonDocument.Parse(json);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, result.Data)); Assert.Equal("actual", result.Metadata.UpstreamModel);
        Assert.False(result.Data.TryGetProperty("invented_tokens", out _));
    }

    /// <summary>公共结果必须为原生对象，不把空值或非对象伪装成空向量集合。</summary>
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("1")] [InlineData("\"text\"")]
    public void Result_RejectsNonObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => new EmbeddingResult(document.RootElement, new("public", "actual", "native")));
    }

    /// <summary>完整原生参数在序列化时与公共 model 并列，不增加 input 包装层。</summary>
    [Fact]
    public void Request_SerializesNativeFieldsWithoutReinterpretation()
    {
        var request = new EmbeddingRequest { Model = "public", Parameters = new()
        {
            ["input"] = JsonSerializer.SerializeToElement(new[] { new[] { 1, 2 }, new[] { 3 } }),
            ["encoding_format"] = JsonSerializer.SerializeToElement("base64"), ["dimensions"] = JsonSerializer.SerializeToElement(2),
            ["future"] = JsonSerializer.SerializeToElement(new { enabled = false, value = (string?)null })
        } };
        using var expected = JsonDocument.Parse("""{"model":"public","input":[[1,2],[3]],"encoding_format":"base64","dimensions":2,"future":{"enabled":false,"value":null}}""");
        Assert.True(JsonElement.DeepEquals(expected.RootElement, JsonSerializer.SerializeToElement(request)));
    }

    /// <summary>JSON 字段严格区分大小写，不继承调用者字典的忽略大小写语义来误删扩展字段。</summary>
    [Fact]
    public async Task NativeKeys_KeepExactCaseDespiteDictionaryComparer()
    {
        using var adapter = new StubEmbeddingAdapter("native", (request, _) => Task.FromResult(
            new EmbeddingResult(JsonSerializer.SerializeToElement(request), new(request.Model, "actual", "native"))));
        await using var client = new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "public", Targets = [adapter] }] }).Build();
        var result = await client.Embedding.EmbedAsync(new() { Model = "public", Parameters = new(StringComparer.OrdinalIgnoreCase)
            { ["MODEL"] = JsonSerializer.SerializeToElement("extension") } }, TestContext.Current.CancellationToken);
        Assert.Equal("public", result.Data.GetProperty("model").GetString()); Assert.Equal("extension", result.Data.GetProperty("MODEL").GetString());
    }
}
