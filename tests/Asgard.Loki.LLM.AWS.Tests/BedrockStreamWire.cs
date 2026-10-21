using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.AWS.Tests;

/// <summary>测试侧完整原生事件/二进制数据，不调用生产转换器或帧写出器生成预期。</summary>
internal static class BedrockStreamWire
{
    /// <summary>共享测试数据分别提供两个完整原生协议形状。</summary>
    internal static JsonNode Read(string name)
    {
        using var stream = typeof(BedrockStreamWire).Assembly.GetManifestResourceStream("BedrockStreamConversion.Fixture")!;
        return JsonNode.Parse(stream)![name]!;
    }
    /// <summary>各自独立持有金样副本，负例不会污染后续调用。</summary>
    internal static List<JsonObject> Rows(bool nova) => Read(nova ? "Nova" : "Converse").AsArray().Select(item => item!.AsObject()).ToList();
    /// <summary>Nova 必须保持完整信封，仅负例修改工具取得其成员。</summary>
    internal static JsonObject Payload(JsonObject row, bool nova) => nova ? row["data"]![row["eventType"]!.GetValue<string>()]!.AsObject() : row["data"]!.AsObject();
    /// <summary>只用现有测试侧 CRC/Base64 编码器构造源 HTTP，完整目标仍由独立原生声明比较。</summary>
    internal static byte[] Bytes(bool nova, IEnumerable<JsonObject>? rows = null)
    {
        using var output = new MemoryStream();
        foreach (var row in rows ?? Rows(nova))
        {
            var json = row["data"]!.ToJsonString();
            output.Write(nova ? StreamFixture.Chunk(json) : StreamFixture.Frame("event", row["eventName"]!.GetValue<string>(), json));
        }
        return output.ToArray();
    }
}
