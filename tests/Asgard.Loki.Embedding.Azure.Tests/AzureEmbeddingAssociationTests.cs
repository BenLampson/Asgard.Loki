using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.Embedding.Azure.Tests;

/// <summary>验证返回向量覆盖原始批输入且保持编码和维度，不能通过重排/填补掩盖关联错误。</summary>
public sealed class AzureEmbeddingAssociationTests
{
    /// <summary>重复、缺失、超界索引或维度/编码错误不成为有效原生批向量。</summary>
    [Theory]
    [InlineData("duplicate")] [InlineData("missing")] [InlineData("outside")] [InlineData("width")]
    [InlineData("encoding")] [InlineData("mixed")] [InlineData("base64")] [InlineData("nonfinite")]
    public async Task InvalidNativeVectorAssociationFailsWithoutRepair(string kind)
    {
        var data = JsonNode.Parse(AzureEmbeddingFixture.Part("base64_response").GetRawText())!.AsObject(); var vectors = data["data"]!.AsArray();
        switch (kind)
        {
            case "duplicate": vectors[1]!["index"] = 1; break;
            case "missing": vectors.RemoveAt(1); break;
            case "outside": vectors[1]!["index"] = 2; break;
            case "width": vectors[1]!["embedding"] = "AAAAAA=="; break;
            case "encoding": vectors[0]!["embedding"] = new JsonArray(0, 1); vectors[1]!["embedding"] = new JsonArray(1, 0); break;
            case "mixed": vectors[1]!["embedding"] = new JsonArray(0, 1); break;
            case "base64": vectors[1]!["embedding"] = "not-base64"; break;
            case "nonfinite": vectors[0]!["embedding"] = new JsonArray(JsonNode.Parse("1e400"), 0); vectors[1]!["embedding"] = new JsonArray(0, 1); break;
        }
        using var factory = AzureEmbeddingFixture.Factory(); factory.OnSend = (_, _) => Task.FromResult(AzureEmbeddingFixture.Json(AzureEmbeddingFixture.Element(data.ToJsonString())));
        var request = AzureEmbeddingFixture.Request(1); if (kind == "nonfinite") request.Parameters["encoding_format"] = System.Text.Json.JsonSerializer.SerializeToElement("float");
        await Assert.ThrowsAsync<InvalidDataException>(() => new AzureEmbeddingAdapter(factory, AzureEmbeddingFixture.Options()).EmbedAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, factory.Calls); Assert.Equal(1, factory.Disposals);
    }
}
