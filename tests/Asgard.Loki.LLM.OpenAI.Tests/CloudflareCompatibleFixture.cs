using System.Reflection;
using System.Text.Json;

namespace Asgard.Loki.LLM.OpenAI.Tests;

/// <summary>从独立 Cloudflare 字面样本加载原始契约，不调用生产转换器生成预期。</summary>
internal static class CloudflareCompatibleFixture
{
    internal static string Text(string name)
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cloudflare." + name)!;
        using var reader = new StreamReader(source); return reader.ReadToEnd();
    }
    internal static JsonElement Part(string name, bool responses)
    { using var document = JsonDocument.Parse(Text(responses ? "responses.json" : "chat.json")); return document.RootElement.GetProperty(name).Clone(); }
}
