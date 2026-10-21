using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立维护原生纯文本文件、前后文本与工具历史；预期不由转换器生成。</summary>
internal static class TextFileFixtures
{
    internal static LlmProtocol[] Protocols => [LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];
    internal const string Text = "首😀é\r\n尾\0\uFEFF";
    internal const string Encoded = "6aaW8J+YgGXMgQ0K5bC+AO+7vw==";
    /// <summary>三协议的六个方向；Chat 没有非 PDF 文件容器。</summary>
    internal static TheoryData<LlmProtocol, LlmProtocol> Directions()
    {
        TheoryData<LlmProtocol, LlmProtocol> data = [];
        foreach (var source in Protocols) foreach (var target in Protocols) if (source != target) data.Add(source, target); return data;
    }
    /// <summary>每次加载独立完整请求快照，原生类型/工具关联由样本明确指定。</summary>
    internal static JsonObject Read(LlmProtocol protocol)
    {
        using var stream = typeof(TextFileFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.files.utf8-text.json") ?? throw new InvalidDataException("纯文本文件样本缺失。");
        return JsonNode.Parse(stream)![protocol.ToString()]!.AsObject();
    }
    /// <summary>明确固定编码和解码字节上限，不根据样本内容推测编码。</summary>
    internal static LlmConversionOptions Options => new() { TextFileEncoding = LlmTextFileEncoding.Utf8 };
    /// <summary>通过既有独立原生定位器获得第一份文件。</summary>
    internal static JsonObject File(JsonObject request, LlmProtocol protocol) => InputFileFixtures.File(request, protocol);
    /// <summary>设置原生文件源数据，不调用生产编码/映射代码。</summary>
    internal static void Data(JsonObject request, LlmProtocol protocol, string value)
        => File(request, protocol)[protocol == LlmProtocol.Responses ? "file_data" : "data"] = protocol == LlmProtocol.Responses ? "data:text/plain;base64," + value : value;
}
