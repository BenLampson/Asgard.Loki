using System.Buffers.Text;
using System.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>只转换声明 UTF-8 的 text/plain 文件；文件语义独立于普通消息文本。</summary>
internal static class TextFileMapping
{
    /// <summary>本检查点唯一明确映射的纯文本 MIME。</summary>
    internal const string Mime = "text/plain";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>先检查实际解码字节数，再分配缓冲并严格解码，保留原始 Base64。</summary>
    internal static JsonObject ReadBase64(string data, ConversionDocument doc)
    {
        RequireEncoding(doc);
        if (!Base64.IsValid(data.AsSpan(), out var length) || length == 0) throw new ArgumentException("纯文本文件必须包含非空标准 Base64。");
        Size(length, doc);
        string text;
        try { text = StrictUtf8.GetString(Convert.FromBase64String(data)); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("纯文本文件不符合声明的 UTF-8 编码。", error); }
        return Block(data, text);
    }

    /// <summary>Claude 原生 text 源已经是字符串，严格编码前验证代理项和实际字节上限。</summary>
    internal static JsonObject ReadText(string text, ConversionDocument doc)
    {
        RequireEncoding(doc);
        try
        {
            var length = StrictUtf8.GetByteCount(text);
            if (length == 0) throw new ArgumentException("纯文本文件不得为空。");
            Size(length, doc); return Block(Convert.ToBase64String(StrictUtf8.GetBytes(text)), text);
        }
        catch (EncoderFallbackException error) { throw new InvalidDataException("纯文本文件包含无效 Unicode。", error); }
    }

    /// <summary>文件内容保持独立文档，不转成 Chat 消息，不把文件名伪装成标题。</summary>
    internal static JsonObject Write(JsonObject block, LlmProtocol protocol, ConversionDocument doc)
    {
        if (protocol == LlmProtocol.ChatCompletions) Reject("text_file_target_not_representable");
        if (protocol == LlmProtocol.Responses)
        {
            var file = new JsonObject { ["type"] = "input_file", ["file_data"] = "data:" + Mime + ";base64," + Text(block["data"]) };
            Copy(block, "filename", file, "filename"); return file;
        }
        if (block.ContainsKey("filename")) Loss(doc, "$content[].file.filename", "file_name_not_representable");
        return protocol == LlmProtocol.ClaudeMessages
            ? new() { ["type"] = "document", ["source"] = new JsonObject { ["type"] = "text", ["media_type"] = Mime, ["data"] = Text(block["text"]) } }
            : new() { ["inlineData"] = new JsonObject { ["mimeType"] = Mime, ["data"] = Text(block["data"]) } };
    }

    /// <summary>私有中间块同时保留文件字节表达和已验证文本，避免重复解码或丢失 BOM。</summary>
    private static JsonObject Block(string data, string text) => new() { ["type"] = "file", ["mime"] = Mime, ["data"] = data, ["text"] = text };
    /// <summary>不能以普通元数据损耗许可替代编码声明。</summary>
    private static void RequireEncoding(ConversionDocument doc) { if (doc.TextFileEncoding != LlmTextFileEncoding.Utf8) Reject("text_file_encoding_required"); }
    /// <summary>限制解码数据，不按 Base64 字符数或 UTF-16 字符数计量，也不截断。</summary>
    private static void Size(int length, ConversionDocument doc) { if (length > doc.MaxTextFileBytes) Reject("text_file_byte_limit"); }
    /// <summary>只报告边界，不把用户文档内容写入诊断。</summary>
    private static void Reject(string code) => throw new LlmConversionException([new(code, "$content[].file", "纯文本文件缺少编码声明、超过限额或无法保留目标文档语义。")]);
}
