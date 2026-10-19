using System.Buffers.Text;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>显式转换用户提供的 PDF 输入；不下载、上传、解析文件或复用供应商私有文件引用。</summary>
internal static class InputFileMapping
{
    private const string PdfMime = "application/pdf";

    /// <summary>读取 OpenAI 文件或 Claude 文档的原生结构，未知元数据仍逐字段诊断。</summary>
    internal static void Read(JsonObject block, LlmProtocol protocol, ConversionMessage message, ConversionDocument doc)
    {
        RequireUser(message);
        var type = Text(block["type"]);
        if (protocol == LlmProtocol.ClaudeMessages && type == "document")
        {
            Check(block, doc, "$content[].document", "type", "source");
            var source = Object(block["source"]); var sourceType = Text(source["type"]);
            if (sourceType == "base64")
            {
                Check(source, doc, "$content[].source", "type", "media_type", "data");
                message.Blocks.Add(Inline(Text(source["media_type"]), Text(source["data"])));
            }
            else if (sourceType == "url")
            {
                Check(source, doc, "$content[].source", "type", "url"); var url = Text(source["url"]);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    throw new ArgumentException("PDF URL 必须是明确的 HTTP(S) 地址。");
                // Claude 的 URL 文档契约是 PDF；向 Responses 的更广文件输入传原引用，无 SDK 下载。
                message.Blocks.Add(new() { ["type"] = "file", ["url"] = url });
            }
            else if (sourceType == "file") Reference();
            else Reject("unsupported_document_source", "$content[].source.type");
            return;
        }
        JsonObject file;
        if (protocol == LlmProtocol.ChatCompletions && type == "file")
        {
            Check(block, doc, "$content[].file", "type", "file"); file = Object(block["file"]);
            Check(file, doc, "$content[].file", "file_data", "file_id", "filename");
        }
        else if (protocol == LlmProtocol.Responses && type == "input_file")
        {
            file = block; Check(file, doc, "$content[].input_file", "type", "file_data", "file_id", "file_url", "filename");
        }
        else { Reject("unsupported_file_content", "$content[].type"); return; }
        if (new[] { "file_data", "file_id", "file_url" }.Count(file.ContainsKey) != 1)
            throw new ArgumentException("文件输入必须包含一种明确来源。");
        if (file.ContainsKey("file_id")) { Reference(); return; }
        if (file.ContainsKey("file_url")) { Reject("media_resolution_required", "$content[].file_url"); return; }
        var dataUrl = Text(file["file_data"]); var separator = dataUrl.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || separator < 5)
        { Reject("file_mime_required", "$content[].file_data"); return; }
        var converted = Inline(dataUrl[5..separator], dataUrl[(separator + 8)..]);
        if (file.ContainsKey("filename")) converted["filename"] = Text(file["filename"]);
        message.Blocks.Add(converted);
    }

    /// <summary>只为已核对的 PDF MIME 添加新映射，其他 Gemini 媒体继续走原有诊断路径。</summary>
    internal static bool ReadGemini(JsonObject inline, ConversionMessage message, ConversionDocument doc)
    {
        if (!string.Equals(Text(inline["mimeType"]), PdfMime, StringComparison.OrdinalIgnoreCase)) return false;
        RequireUser(message); Check(inline, doc, "$parts[].inlineData", "mimeType", "data");
        message.Blocks.Add(Inline(Text(inline["mimeType"]), Text(inline["data"]))); return true;
    }

    /// <summary>输出目标原生 PDF 块；文件名损耗和引用解析要求不会用伪造字段掩盖。</summary>
    internal static JsonObject Write(JsonObject block, LlmProtocol protocol, ConversionDocument doc)
    {
        if (block.ContainsKey("url"))
        {
            if (protocol != LlmProtocol.Responses) Reject("media_resolution_required", "$content[].file.url");
            return new() { ["type"] = "input_file", ["file_url"] = Text(block["url"]) };
        }
        var data = Text(block["data"]);
        if (protocol is LlmProtocol.ChatCompletions or LlmProtocol.Responses)
        {
            var file = new JsonObject { ["file_data"] = "data:" + PdfMime + ";base64," + data };
            Copy(block, "filename", file, "filename");
            if (protocol == LlmProtocol.ChatCompletions) return new() { ["type"] = "file", ["file"] = file };
            file["type"] = "input_file"; return file;
        }
        if (block.ContainsKey("filename")) Loss(doc, "$content[].file.filename", "file_name_not_representable");
        return protocol == LlmProtocol.ClaudeMessages
            ? new() { ["type"] = "document", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = PdfMime, ["data"] = data } }
            : new() { ["inlineData"] = new JsonObject { ["mimeType"] = PdfMime, ["data"] = data } };
    }

    /// <summary>只检查显式 MIME 和 Base64，不分配整份解码缓冲，也不宣称 PDF 文件结构已经验证。</summary>
    private static JsonObject Inline(string mime, string data)
    {
        if (!string.Equals(mime, PdfMime, StringComparison.OrdinalIgnoreCase)) Reject("unsupported_file_mime", "$content[].file.mime");
        if (!Base64.IsValid(data.AsSpan(), out var length) || length == 0) throw new ArgumentException("文件数据必须是非空的标准 Base64。");
        return new() { ["type"] = "file", ["data"] = data };
    }

    /// <summary>此检查点只支持用户输入文件，不把生成文件或系统指令移到用户消息中。</summary>
    private static void RequireUser(ConversionMessage message)
    {
        if (message.Role != "user") Reject("unsupported_file_role", "$content[].file");
    }

    /// <summary>私有文件 ID/URI 的归属不能由协议名推断，普通损耗许可也不授权跨目标复用。</summary>
    internal static void Reference() => Reject("provider_file_reference", "$content[].file_reference");

    /// <summary>需要补充调用方数据或独立原生接口的边界始终明确失败。</summary>
    private static void Reject(string code, string path) => throw new LlmConversionException([new(code, path, "文件转换缺少可等价表达的数据；请使用明确的 PDF 数据或原生接口。")]);
}
