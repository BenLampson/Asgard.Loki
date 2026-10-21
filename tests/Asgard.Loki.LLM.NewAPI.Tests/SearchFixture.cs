using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>独立原生请求和字节窗口，不通过实现生成协议预期。</summary>
internal static class SearchFixture
{
    internal static JsonElement Window { get { using var stream = typeof(SearchFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.NewAPI.Tests.Fixtures.search.json")!; using var document = JsonDocument.Parse(stream); return document.RootElement.Clone(); } }
    internal static JsonElement Profile(int profile = 0) => Window.GetProperty("profiles")[profile];
    internal static JsonObject Parameters(string kind = "omitted") => JsonNode.Parse(Window.GetProperty("requests").GetProperty(kind).GetRawText())!.AsObject();
    internal static NewApiSearchOptions Options(int profile = 0) { var p = Profile(profile); return new() { BaseAddress = new(p.GetProperty("root").GetString()!), UpstreamModel = p.GetProperty("model").GetString()!, TargetId = p.GetProperty("target").GetString()!, Version = p.GetProperty("version").GetString()!, HttpClientName = p.GetProperty("client").GetString()! }; }
    internal static LlmCredential Credential(int profile = 0) => new("credential-"+profile, Profile(profile).GetProperty("key").GetString()!);
    internal static byte[] Bytes(JsonElement value) => value.TryGetProperty("base64", out var encoded) ? Convert.FromBase64String(encoded.GetString()!) : new UTF8Encoding(false, true).GetBytes(value.GetProperty("body").GetString()!);
    internal static HttpResponseMessage Reply(Stream body, int status = 200, string? type = "application/json", long? length = null)
    { var content = new SearchContent(body); if (type is not null) content.Headers.TryAddWithoutValidation("Content-Type", type); content.Headers.ContentLength = length; return new((HttpStatusCode)status) { Content = content }; }
    internal static void OriginalRequest(JsonElement actual, JsonObject parameters, int profile = 0)
    { var expected = parameters.DeepClone().AsObject(); expected["model"] = Profile(profile).GetProperty("model").GetString(); Xunit.Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), actual)); }
}
