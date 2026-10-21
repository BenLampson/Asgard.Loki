using System.Text.Json;

namespace Asgard.Loki.Embedding.OpenAI.Tests;

/// <summary>九个显式目标的独立完整协议样本，不根据供应商品牌分支或生成生产转换结果。</summary>
internal static class CompatibleEmbeddingProfiles
{
    /// <summary>每个固定目标分别验证文本标量和文本批输入。</summary>
    public static IEnumerable<object[]> Calls()
    {
        for (var i = 0; i < Part("profiles").GetArrayLength(); i++) { yield return [i, false]; yield return [i, true]; }
    }
    /// <summary>每个目标分别验证原生 HTTP 错误及不重试约定。</summary>
    public static IEnumerable<object[]> Targets() => Enumerable.Range(0, Part("profiles").GetArrayLength()).Select(i => new object[] { i });
    internal static JsonElement Part(string name)
    {
        using var stream = typeof(CompatibleEmbeddingProfiles).Assembly.GetManifestResourceStream(typeof(CompatibleEmbeddingProfiles).Namespace + ".Fixtures.compatible-profiles.json")!;
        using var source = JsonDocument.Parse(stream); return source.RootElement.GetProperty(name).Clone();
    }
    internal static JsonElement Wire(int index, bool batch) => Part("profiles")[index].GetProperty(batch ? "batch_request" : "scalar_request");
    internal static OpenAiEmbeddingOptions Options(int index)
    {
        var profile = Part("profiles")[index];
        return new()
        {
            Id = profile.GetProperty("id").GetString()!, Model = profile.GetProperty("model").GetString()!, ApiKey = profile.GetProperty("api_key").GetString()!,
            BaseAddress = new(profile.GetProperty("base_address").GetString()!), HttpClientName = profile.GetProperty("http_client_name").GetString()!
        };
    }
    internal static EmbeddingRequest Request(int index, bool batch) => new()
    { Model = "public-vectors", Parameters = Wire(index, batch).EnumerateObject().Where(p => p.Name != "model").ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal) };
}
