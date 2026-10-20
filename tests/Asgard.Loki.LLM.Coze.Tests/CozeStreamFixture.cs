using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Coze.Tests;

/// <summary>独立原生帧窗口的离线故障变体，不调用生产转换或状态代码生成预期。</summary>
internal static class CozeStreamFixture
{
    internal static List<(string Name, string Data)> Events()
    {
        using var doc = JsonDocument.Parse(CozeFixture.Text("events.json"));
        return doc.RootElement.EnumerateArray().Select(e => (e.GetProperty("event").GetString()!, e.GetProperty("data").GetString()!)).ToList();
    }
    internal static string Wire(IEnumerable<(string Name, string Data)> frames)
    {
        var builder = new StringBuilder(); foreach (var frame in frames) builder.Append("event: ").Append(frame.Name).Append("\ndata: ").Append(frame.Data).Append("\n\n"); return builder.ToString();
    }
    internal static void Change(List<(string Name, string Data)> frames, int index, string field, string value)
    { var body = JsonNode.Parse(frames[index].Data)!.AsObject(); body[field] = value; frames[index] = (frames[index].Name, body.ToJsonString()); }
    internal static string Prefix() => Wire(Events().Take(2));
    internal static string Suffix() => Wire(Events().Skip(8));
}
