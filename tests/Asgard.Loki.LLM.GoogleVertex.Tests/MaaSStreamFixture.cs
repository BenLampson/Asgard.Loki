using System.Reflection;
using System.Text;
using Asgard.Loki.LLM.Ollama.Tests;

namespace Asgard.Loki.LLM.GoogleVertex.Tests;

/// <summary>独立原始 MaaS SSE 及替代传输，消费方法不改变任何原生事件。</summary>
internal static class MaaSStreamFixture
{
    internal static string Sse()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Asgard.Loki.LLM.GoogleVertex.Tests.Fixtures.maas.sse")!; using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    internal static string Frame(string data) => "data: " + data + "\n\n";
    internal const string Start = "{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}";
    internal const string Finish = "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}";
    internal static HttpResponseMessage Reply(string text) => StreamingTests.Reply(new OllamaBodyStream(Encoding.UTF8.GetBytes(text)));
    internal static async Task Consume(GoogleVertexMaaSLlmProvider provider, CancellationToken token, Action<ChatCompletionEvent>? observe = null)
    {
        await foreach (var item in provider.StreamAsync(MaaSFixture.Request(), MaaSFixture.Context(), token)) observe?.Invoke(item);
    }
}
