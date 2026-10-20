using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.DeepSeek.Tests;

/// <summary>独立原生工具窗口与替代 HTTP，所有页面 URL 都只作为请求/结果文本。</summary>
internal static class ToolsFixture
{
    internal const string Key = "fixture-native-tool-key";
    internal static JsonElement Part(string name)
    { using var stream = typeof(ToolsFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.DeepSeek.Tests.Fixtures.tools.json")!; using var source = JsonDocument.Parse(stream); return source.RootElement.GetProperty(name).Clone(); }
    internal static JsonObject Request(bool fetch = false, int index = 0) => JsonNode.Parse(Part(fetch ? "fetch_requests" : "search_requests")[index].GetRawText())!.AsObject();
    internal static JsonElement Response(bool fetch = false, int index = 0) => Part(fetch ? "fetch_responses" : "search_responses")[index];
    internal static DeepSeekToolsOptions Options(int request = 1024 * 1024, int response = 16 * 1024 * 1024, string? root = null, string key = Key, string name = "fixture-deepseek-tools")
        => new() { BaseAddress = new(root ?? Part("root").GetString()!), ApiKey = key, HttpClientName = name, MaximumRequestBytes = request, MaximumResponseBytes = response };
    internal static ToolsHttpFactory Factory(bool fetch = false, int index = 0) => new() { OnSend = (_, _) => Task.FromResult(Json(Response(fetch, index))) };
    internal static HttpResponseMessage Json(JsonElement data, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(data.GetRawText(), Encoding.UTF8, "application/json") };
    internal static JsonElement Element(string raw) { using var source = JsonDocument.Parse(raw); return source.RootElement.Clone(); }
    internal static Task<DeepSeekToolsResult> Invoke(DeepSeekToolsClient client, CancellationToken token) => Invoke(client, false, token);
    internal static Task<DeepSeekToolsResult> Invoke(DeepSeekToolsClient client, bool fetch, CancellationToken token, JsonObject? request = null)
        => fetch ? client.FetchAsync(request ?? Request(true), token) : client.SearchAsync(request ?? Request(), token);
}
