using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.NewAPI.Tests;

/// <summary>独立原生搜索参数与完整字节窗口，不从 SDK 输出生成预期。</summary>
internal static class ConfiguredSearchFixture
{
    internal static JsonElement Window { get { using var source = typeof(ConfiguredSearchFixture).Assembly.GetManifestResourceStream("Asgard.Loki.LLM.NewAPI.Tests.Fixtures.configured-search.json")!; using var json = JsonDocument.Parse(source); return json.RootElement.Clone(); } }
    internal static JsonElement Profile(int index) => Window.GetProperty("profiles")[index];
    internal static JsonObject Parameters() => JsonNode.Parse(Window.GetProperty("requests").GetProperty("true").GetRawText())!.AsObject();
    internal static LlmCredential Credential(int profile) => profile == 1 ? LlmCredential.Anonymous("configured-key") : new("configured-key", Profile(profile).GetProperty("key").GetString()!);
    internal static NewApiSearchOptions Options(int profile = 0) => new()
    {
        BaseAddress = new(Window.GetProperty("root").GetString()!), UpstreamModel = Window.GetProperty("model").GetString()!, TargetId = "configured-search", Version = "configured-v1", HttpClientName = "Explicit.Configured.Search",
        Authentication = Enum.Parse<NewApiSearchAuthentication>(Profile(profile).GetProperty("authentication").GetString()!),
        Transport = new() { SearchPath = Window.GetProperty("path").GetString()!, CredentialName = Profile(profile).GetProperty("name").GetString(), CredentialPrefix = Profile(profile).GetProperty("prefix").GetString()!, CredentialSuffix = Profile(profile).GetProperty("suffix").GetString()! }
    };
}
