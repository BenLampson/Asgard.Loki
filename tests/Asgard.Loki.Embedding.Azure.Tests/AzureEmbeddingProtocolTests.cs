using System.Text.Json;
using Xunit;

namespace Asgard.Loki.Embedding.Azure.Tests;

/// <summary>部署/版本/独立认证、四种原生输入、向量索引和完整结果保真。</summary>
public sealed class AzureEmbeddingProtocolTests
{
    /// <summary>输入文本/token 边界、显式编码/维度和未知参数原样发送，不从路由或默认值补字段。</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task FourNativeInputFormsAndCompleteResultsRemainOriginal(int index)
    {
        var bodyModel = index == 3 ? "explicit-native-body-model" : null; var name = AzureEmbeddingFixture.ResultName(index);
        using var factory = AzureEmbeddingFixture.Factory(); factory.ConfigureClient = client =>
        { client.DefaultRequestHeaders.Authorization = new("Bearer", "unrelated-key"); client.DefaultRequestHeaders.Add("api-key", "unrelated-key"); client.DefaultRequestHeaders.Add("OpenAI-Organization", "unrelated-org"); };
        factory.OnSend = async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method); Assert.Equal(AzureEmbeddingFixture.Part("endpoint").GetString(), message.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-azure-embedding-key", Assert.Single(message.Headers.GetValues("api-key"))); Assert.Null(message.Headers.Authorization); Assert.False(message.Headers.Contains("OpenAI-Organization"));
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType); Assert.Equal("application/json", Assert.Single(message.Headers.Accept).MediaType);
            Assert.True(JsonElement.DeepEquals(AzureEmbeddingFixture.Part("requests")[index], AzureEmbeddingFixture.Element(await message.Content.ReadAsStringAsync(token))));
            return AzureEmbeddingFixture.Json(AzureEmbeddingFixture.Part(name));
        };
        var options = AzureEmbeddingFixture.Options(bodyModel); Assert.DoesNotContain("fixture-azure-embedding-key", JsonSerializer.Serialize(options));
        var result = await new AzureEmbeddingAdapter(factory, options).EmbedAsync(AzureEmbeddingFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(AzureEmbeddingFixture.Part(name), result.Data)); Assert.Equal(new EmbeddingResultMetadata("vectors", "embed.deploy-01", "azure-embedding-test"), result.Metadata);
        Assert.Equal("native-resolved-embedding-version", result.Data.GetProperty("model").GetString()); Assert.Equal("9007199254740993", result.Data.GetProperty("usage").GetProperty("future").GetRawText());
        if (index != 1) Assert.Equal("0.123456789012345678901", result.Data.GetProperty("data")[0].GetProperty("embedding")[0].GetRawText());
        Assert.Equal("azure-embedding-tests", factory.ClientName); Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals); Assert.False(factory.Disposed);
    }
    /// <summary>公共原生批路由保留返回的重排顺序和每个原始 index，不按位置重新关联向量。</summary>
    [Theory]
    [InlineData(1)] [InlineData(3)]
    public async Task ExistingEmbeddingRouteKeepsOriginalBatchIndexAssociation(int index)
    {
        using var factory = AzureEmbeddingFixture.Factory(AzureEmbeddingFixture.ResultName(index)); var adapter = new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options(index == 3 ? "explicit-native-body-model" : null));
        await using var client = new LokiClientBuilder().AddEmbedding(new() { Routes = [new() { Model = "vectors", Targets = [adapter] }] }).Build();
        var result = await client.Embedding.EmbedAsync(AzureEmbeddingFixture.Request(index), TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Data.GetProperty("data")[0].GetProperty("index").GetInt32()); Assert.Equal(0, result.Data.GetProperty("data")[1].GetProperty("index").GetInt32());
        Assert.True(JsonElement.DeepEquals(AzureEmbeddingFixture.Part(AzureEmbeddingFixture.ResultName(index)), result.Data)); Assert.Equal(1, factory.Calls);
    }
    /// <summary>原生文本空格不修剪，最小请求不注入 body model、编码或维度默认值。</summary>
    [Fact]
    public async Task MinimalInputDoesNotAcquireDefaults()
    {
        using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = async (message, token) =>
        { var data = AzureEmbeddingFixture.Element(await message.Content!.ReadAsStringAsync(token)); Assert.Single(data.EnumerateObject()); Assert.Equal("  original text  ", data.GetProperty("input").GetString()); return AzureEmbeddingFixture.Json(AzureEmbeddingFixture.Part("float_response")); };
        await new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(new() { Model = "vectors", Parameters = new() { ["input"] = JsonSerializer.SerializeToElement("  original text  ") } }, TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
    /// <summary>源文档/字典可在工厂调用时释放或清空，不改变已经冻结的完整请求。</summary>
    [Fact]
    public async Task SnapshotPrecedesFactoryAndSourceDocumentDisposal()
    {
        var request = AzureEmbeddingFixture.Request(); using var source = JsonDocument.Parse(request.Parameters["future"].GetRawText()); request.Parameters["future"] = source.RootElement;
        using var factory = AzureEmbeddingFixture.Factory(); factory.OnCreate = () => { source.Dispose(); request.Parameters.Clear(); };
        factory.OnSend = async (message, token) =>
        { Assert.True(JsonElement.DeepEquals(AzureEmbeddingFixture.Part("requests")[0], AzureEmbeddingFixture.Element(await message.Content!.ReadAsStringAsync(token)))); return AzureEmbeddingFixture.Json(AzureEmbeddingFixture.Part("float_response")); };
        await new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(request, TestContext.Current.CancellationToken); Assert.Empty(request.Parameters); Assert.Equal(1, factory.Calls);
    }
    /// <summary>版本是单一查询值，不能注入其他查询参数，部署字面点号不会被历史规则移除。</summary>
    [Fact]
    public async Task ExplicitVersionIsEscapedOnceWithoutDeploymentRewriting()
    {
        using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = (message, _) =>
        { Assert.Equal("https://azure-embeddings.example.invalid/prefix/openai/deployments/embed.deploy-01/embeddings?api-version=2024-10-21%26trace%3Don", message.RequestUri!.AbsoluteUri); return Task.FromResult(AzureEmbeddingFixture.Json(AzureEmbeddingFixture.Part("float_response"))); };
        await new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options(version: "2024-10-21&trace=on")).EmbedAsync(AzureEmbeddingFixture.Request(), TestContext.Current.CancellationToken); Assert.Equal(1, factory.Calls);
    }
}
