using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Volcengine.Tests;

/// <summary>独立完整 Bot 资源，不用供应商输出生成预期。</summary>
internal static class ArkBotFixture
{
    internal static JsonElement BotPart(string name) { using var json = JsonDocument.Parse(ArkFixture.Text("bot.json")); return json.RootElement.GetProperty(name).Clone(); }
    internal static VolcengineBotOptions BotOptions() => new() { BaseAddress = new(BotPart("root").GetString()!), BotId = BotPart("bot_id").GetString()!, TargetId = "native-published-app", CredentialId = "native-bot-key" };
    internal static JsonObject BotPayload() => JsonNode.Parse(BotPart("caller").GetRawText())!.AsObject();
    internal static List<string> BotFrames() { using var json = JsonDocument.Parse(ArkFixture.Text("bot-events.json")); return json.RootElement.EnumerateArray().Select(x => x.GetRawText()).ToList(); }
    internal static async Task<List<VolcengineBotEvent>> BotStream(VolcengineBotClient client, CancellationToken token, Action<VolcengineBotEvent>? observe = null)
    { List<VolcengineBotEvent> result = []; await foreach (var item in client.StreamAsync(BotPayload(), token)) { observe?.Invoke(item); result.Add(item); } return result; }
}
