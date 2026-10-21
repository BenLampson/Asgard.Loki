using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Dify.Tests;

/// <summary>成功窗口来自独立字面资源；负例只改变明确协议事实，不由生产转换生成期望结果。</summary>
internal static class DifyStreamFixture
{
    internal static List<string> Frames(string profile = "chatflow") => DifyFixture.Json(DifyFixture.Text(profile + ".json")).EnumerateArray().Select(x => x.GetRawText()).ToList();
    internal static string Wire(IEnumerable<string> frames) => string.Concat(frames.Select(x => "data: " + x + Environment.NewLine + Environment.NewLine));
    internal static void Change(List<string> frames, int index, string name, object? value)
    { var node = JsonNode.Parse(frames[index])!.AsObject(); node[name] = JsonSerializer.SerializeToNode(value); frames[index] = node.ToJsonString(); }
    internal static async Task<List<DifyChatEvent>> Collect(DifyChatClient client, CancellationToken token, DifyChatRequest? request = null)
    { List<DifyChatEvent> result = []; await foreach (var item in client.StreamAsync(request ?? DifyFixture.Request(), token)) result.Add(item); return result; }
}
