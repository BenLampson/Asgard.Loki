using System.Text.Json.Nodes;
using Xunit;

namespace Asgard.Loki.LLM.Conversion.Tests;

/// <summary>独立维护四协议 PDF 输入/工具历史及完整目标快照；不调用生产转换器构造预期值。</summary>
internal static class InputFileFixtures
{
    /// <summary>四个现有原生协议。</summary>
    internal static LlmProtocol[] Protocols => [LlmProtocol.ChatCompletions, LlmProtocol.Responses, LlmProtocol.ClaudeMessages, LlmProtocol.Gemini];

    /// <summary>十二个独立转换方向。</summary>
    internal static TheoryData<LlmProtocol, LlmProtocol> Directions()
    {
        TheoryData<LlmProtocol, LlmProtocol> data = [];
        foreach (var source in Protocols) foreach (var target in Protocols) if (source != target) data.Add(source, target);
        return data;
    }

    /// <summary>每次获取独立可变副本；二进制样本用于字节传输验证，不验证完整 PDF 文档可渲染。</summary>
    internal static JsonObject Read(LlmProtocol protocol, bool golden = false)
    {
        using var stream = typeof(InputFileFixtures).Assembly.GetManifestResourceStream(
            "Asgard.Loki.LLM.Conversion.Tests.Fixtures.files.inline-pdf.json") ?? throw new InvalidDataException("文件协议样本缺失。");
        return JsonNode.Parse(stream)![golden ? "golden" : "source"]![protocol.ToString()]!.AsObject();
    }

    /// <summary>定位第一条用户消息的内容数组，不依赖生产映射。</summary>
    internal static JsonArray Parts(JsonObject request, LlmProtocol protocol) => (protocol switch
    {
        LlmProtocol.Responses => request["input"]![0]!["content"],
        LlmProtocol.Gemini => request["contents"]![0]!["parts"],
        _ => request["messages"]![0]!["content"]
    })!.AsArray();

    /// <summary>定位原生文件数据对象，Chat 嵌套 file 与其他原生结构保持分离。</summary>
    internal static JsonObject File(JsonObject request, LlmProtocol protocol)
    {
        var part = Parts(request, protocol)[1]!;
        return (protocol switch { LlmProtocol.ChatCompletions => part["file"], LlmProtocol.ClaudeMessages => part["source"], LlmProtocol.Gemini => part["inlineData"], _ => part })!.AsObject();
    }

    /// <summary>Chat 使用独立的原生文本流；其他协议复用各自已经独立编写的文本样本。</summary>
    internal static JsonObject[] TextStream(LlmProtocol protocol) => protocol != LlmProtocol.ChatCompletions ? HostedSearchFixtures.TextStream(protocol)
        : JsonNode.Parse("""
        [
          {"id":"reply","object":"chat.completion.chunk","model":"model-a","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]},
          {"id":"reply","object":"chat.completion.chunk","model":"model-a","choices":[{"index":0,"delta":{"content":"done"},"finish_reason":null}]},
          {"id":"reply","object":"chat.completion.chunk","model":"model-a","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        ]
        """)!.AsArray().Select(e => e!.AsObject()).ToArray();
}
