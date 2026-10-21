using System.Net;
using System.Reflection;
using System.Text.Json;
using Asgard.Loki.Testing;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>独立原生样本和替代传输，不引入无必要缓存、真实账号或外部网络。</summary>
internal static class CozeFixture
{
    internal const string Token = "fixture-native-token";
    internal static string Text(string name)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.Coze.Tests.Fixtures." + name)!;
        using var reader = new StreamReader(source); return reader.ReadToEnd();
    }
    internal static JsonElement Part(string name)
    { using var doc = JsonDocument.Parse(Text("chat.json")); return doc.RootElement.GetProperty(name).Clone(); }
    internal static CozeChatOptions Options() => new() { BaseAddress = new(Part("root").GetString()!), BotId = Part("bot_id").GetString()!, TargetId = "native-bot", CredentialId = "native-key" };
    internal static CozeChatRequest Request() => JsonSerializer.Deserialize<CozeChatRequest>(Part("caller"))! with { ConversationId = Part("conversation_id").GetString() };
    internal static StubHttpHandler Reply(Stream source, string contentType = "application/json", int status = 200) => new((_, _) =>
    { var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(source) }; result.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType); return Task.FromResult(result); });
    internal static async Task<CozeChatReference> Reference(CozeChatOptions? options = null, string token = Token)
    {
        using var handler = Reply(new CozeBodyStream(System.Text.Encoding.UTF8.GetBytes(Part("ack").GetRawText()))); using var factory = new CozeHttpFactory(handler);
        return (await new CozeChatClient(factory, options ?? Options(), token).CreateAsync(Request(), Xunit.TestContext.Current.CancellationToken)).Reference;
    }
}
