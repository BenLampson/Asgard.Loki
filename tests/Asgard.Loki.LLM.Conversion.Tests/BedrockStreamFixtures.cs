using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立完整原生事件资源和变体工具，不调用生产写出器生成金样。</summary>
internal static class BedrockStreamFixtures
{
    /// <summary>完整数据分别声明 Converse 载荷与 Nova 信封，读取时返回独立对象。</summary>
    internal static JsonNode Read(string name)
    {
        using var stream = typeof(BedrockStreamFixtures).Assembly.GetManifestResourceStream("BedrockStreamConversion.Fixture")!;
        return JsonNode.Parse(stream)![name]!;
    }
    /// <summary>只修改负例所选的原生数据，不改变协议或隐式重写名称。</summary>
    internal static List<JsonObject> Rows(bool nova) => Read(nova ? "Nova" : "Converse").AsArray().Select(item => item!.AsObject()).ToList();
    /// <summary>Converse 内容在 data，Nova 内容在完整 data 的原生联合成员中。</summary>
    internal static JsonObject Payload(JsonObject row, bool nova) => nova ? row["data"]![row["eventType"]!.GetValue<string>()]!.AsObject() : row["data"]!.AsObject();
    /// <summary>原生数据总是完整推入，原始外层名称独立传递。</summary>
    internal static IReadOnlyList<BedrockStreamConversionResult> Push(BedrockProtocolStreamConverter converter, JsonObject row)
        => converter.Push(row["data"]!.AsObject(), row["eventName"]!.GetValue<string>());
    /// <summary>请求上下文的历史 ID 与新流 ID 分开，反向方向按本次固定上游决定。</summary>
    internal static LlmConversionContext Context(bool fromNova)
    {
        var request = BedrockConversionFixtures.Read("v2", fromNova ? "ConverseDirect" : "Nova");
        return LlmProtocolConverter.ConvertRequest(request, fromNova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova,
            fromNova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse, BedrockConversionFixtures.Options()).Context!;
    }
    /// <summary>完整成功才调用 Complete；任何 Push 失败不会尝试伪造收尾。</summary>
    internal static List<BedrockStreamConversionResult> Convert(bool nova, IEnumerable<JsonObject> source, LlmConversionOptions? options = null)
    {
        var converter = new BedrockProtocolStreamConverter(nova ? LlmProtocol.BedrockNova : LlmProtocol.BedrockConverse,
            nova ? LlmProtocol.BedrockConverse : LlmProtocol.BedrockNova, options);
        List<BedrockStreamConversionResult> results = [];
        foreach (var row in source) results.AddRange(Push(converter, row)); results.AddRange(converter.Complete()); return results;
    }
    /// <summary>完整 JSON 和事件类型分别比较，不从实际输出重建预期。</summary>
    internal static void EqualGolden(bool fromNova, IReadOnlyList<BedrockStreamConversionResult> actual)
    {
        var target = Rows(!fromNova); Xunit.Assert.Equal(target.Count, actual.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            Xunit.Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(target[i]["data"]), actual[i].Data), actual[i].Data.GetRawText());
            Xunit.Assert.Equal(target[i][fromNova ? "eventName" : "eventType"]!.GetValue<string>(), actual[i].EventType);
        }
    }
}
