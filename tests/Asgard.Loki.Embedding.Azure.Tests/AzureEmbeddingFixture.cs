using System.Net;
using System.Text;
using System.Text.Json;

namespace Asgard.Loki.Embedding.Azure.Tests;

/// <summary>在生产实现之前独立编写的 Azure 部署式嵌入原始样本。</summary>
internal static class AzureEmbeddingFixture
{
    internal static JsonElement Part(string name)
    {
        using var source = typeof(AzureEmbeddingFixture).Assembly.GetManifestResourceStream("Asgard.Loki.Embedding.Azure.Tests.Fixtures.azure-embeddings.json")!;
        using var document = JsonDocument.Parse(source); return document.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Element(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    internal static EmbeddingRequest Request(int index = 0) => new() { Model = "vectors", Parameters = Part("requests")[index].EnumerateObject().Where(p => p.Name != "model").ToDictionary(p => p.Name, p => p.Value.Clone()) };
    internal static string ResultName(int index) => index switch { 1 => "base64_response", 3 => "batch_float_response", _ => "float_response" };
    internal static AzureEmbeddingOptions Options(string? bodyModel = null, string deployment = "embed.deploy-01", string version = "2024-10-21", string key = "fixture-azure-embedding-key",
        string endpoint = "https://azure-embeddings.example.invalid/prefix/", int requestLimit = 32 * 1024 * 1024, int responseLimit = 64 * 1024 * 1024)
        => new() { Id = "azure-embedding-test", Endpoint = new(endpoint), Deployment = deployment, ApiVersion = version, ApiKey = key, BodyModel = bodyModel,
            HttpClientName = "azure-embedding-tests", MaxRequestBytes = requestLimit, MaxResponseBytes = responseLimit };
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
    internal static AzureEmbeddingTestFactory Factory(string name = "float_response") => new() { OnSend = (_, _) => Task.FromResult(Json(Part(name))) };
}
