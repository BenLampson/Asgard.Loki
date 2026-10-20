using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>独立字面协议样本和替代 HTTP，不使用真实账号、网络或无必要缓存。</summary>
internal static class DifyFixture
{
    internal const string Token = "fixture-dify-app-key";
    internal static string Text(string name)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Dify.Tests.Fixtures." + name)!;
        using var reader = new StreamReader(source); return reader.ReadToEnd();
    }
    internal static JsonElement Part(string name) { using var d = JsonDocument.Parse(Text("chat.json")); return d.RootElement.GetProperty(name).Clone(); }
    internal static DifyChatOptions Options(DifyChatProfile profile = DifyChatProfile.Chatbot) => new() { BaseAddress = new(Part("root").GetString()!), Profile = profile, TargetId = "native-app", CredentialId = "native-app-key" };
    internal static DifyChatRequest Request() => JsonSerializer.Deserialize<DifyChatRequest>(Part("caller"))!;
    internal static JsonElement Json(string text) { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
    internal static StubHttpHandler Reply(Stream source, string type = "application/json", int status = 200) => new((_, _) =>
    { var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(source) }; result.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(type); return Task.FromResult(result); });
    internal static DifyBodyStream Body(string? text = null) => new(Encoding.UTF8.GetBytes(text ?? Part("blocking").GetRawText()));
}
