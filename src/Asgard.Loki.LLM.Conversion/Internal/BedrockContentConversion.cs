using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;
using static Asgard.Loki.LLM.Conversion.Internal.BedrockConversion;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>验证原生共有内容及稳定工具 ID；不下载媒体、执行工具或解释业务 JSON。</summary>
internal static class BedrockContentConversion
{
    /// <summary>系统块只支持已证明共有的文字，缓存点和策略块不能静默移植。</summary>
    internal static JsonArray System(JsonArray source, ConversionDocument doc)
    {
        JsonArray target = [];
        foreach (var node in source)
        {
            var block = Object(node); var path = "$.system[]";
            if (!block.ContainsKey("text") || block.Any(field => field.Key is "cachePoint" or "guardContent")) Reject(path);
            _ = Text(block["text"]); target.Add(Pick(block, doc, path, "text"));
        }
        return target;
    }

    /// <summary>消息顺序、并行同名调用和结果回填均按原始 ID 跟踪，不按名称重配。</summary>
    internal static JsonArray Messages(JsonArray source, ConversionDocument doc)
    {
        if (source.Count == 0) throw new ArgumentException("Bedrock 请求 messages 不能为空。");
        var calls = new ToolCallTracker(); JsonArray target = [];
        foreach (var node in source) target.Add(Message(Object(node), doc, calls, "$.messages[]"));
        return target;
    }

    /// <summary>每个原生消息和内容块独立复制，未知扩展保留逐字段诊断。</summary>
    internal static JsonObject Message(JsonObject source, ConversionDocument doc, ToolCallTracker calls, string path)
    {
        var role = Text(source["role"]);
        if (role is not ("user" or "assistant")) throw new ArgumentException("Bedrock 消息只支持 user/assistant 角色。");
        var target = Pick(source, doc, path, "role", "content"); JsonArray content = [];
        foreach (var node in Array(source["content"])) content.Add(Block(Object(node), role, calls, doc, path + ".content[]"));
        target["content"] = content; return target;
    }

    /// <summary>已确认共有的内容保持容器，不能丢弃签名、文件或策略状态来获得成功。</summary>
    private static JsonObject Block(JsonObject source, string role, ToolCallTracker calls, ConversionDocument doc, string path)
    {
        if (source.Any(field => field.Key is "document" or "reasoningContent" or "cachePoint" or "guardContent"
            or "citationsContent" or "audio" or "searchResult")) Reject(path);
        var kinds = source.Where(field => field.Key is "text" or "image" or "video" or "toolUse" or "toolResult").Select(field => field.Key).ToArray();
        if (kinds.Length != 1) Reject(path, "bedrock_content_union");
        var kind = kinds[0]; var target = Pick(source, doc, path, kind);
        if (kind == "text") _ = Text(source[kind]);
        else if (kind is "image" or "video")
        {
            if (role != "user") Reject(path);
            target[kind] = Media(Object(source[kind]), kind, doc, path + "." + kind);
        }
        else if (kind == "toolUse")
        {
            if (role != "assistant") throw new ArgumentException("工具调用必须位于 assistant 消息。");
            var tool = Object(source[kind]); calls.Register(Text(tool["toolUseId"]), Text(tool["name"])); _ = Object(tool["input"]);
            target[kind] = Pick(tool, doc, path + ".toolUse", "toolUseId", "name", "input");
        }
        else
        {
            if (role != "user") throw new ArgumentException("工具结果必须位于 user 消息。");
            var result = Object(source[kind]); calls.Consume(Text(result["toolUseId"]));
            var copied = Pick(result, doc, path + ".toolResult", "toolUseId", "status", "content");
            if (result.ContainsKey("status") && Text(result["status"]) is not ("success" or "error")) throw new ArgumentException("无效的原生工具结果状态。");
            JsonArray items = [];
            foreach (var item in Array(result["content"]))
            {
                var data = Object(item); var keys = data.Where(field => field.Key is "json" or "text").Select(field => field.Key).ToArray();
                if (keys.Length != 1 || data.Any(field => field.Key is "image" or "video" or "document" or "reasoningContent")) Reject(path + ".toolResult.content[]");
                if (keys[0] == "text") _ = Text(data["text"]);
                items.Add(Pick(data, doc, path + ".toolResult.content[]", keys[0]));
            }
            copied["content"] = items; target[kind] = copied;
        }
        return target;
    }

    /// <summary>保留原生 Base64 或调用者 S3 引用，不借用 URL 抓取或假设其他供应商文件 ID 可用。</summary>
    private static JsonObject Media(JsonObject source, string kind, ConversionDocument doc, string path)
    {
        var target = Pick(source, doc, path, "format", "source"); _ = Text(source["format"]);
        var media = Object(source["source"]);
        if (media.ContainsKey("bytes") == media.ContainsKey("s3Location") || kind == "image" && media.ContainsKey("s3Location")) Reject(path + ".source");
        var body = Pick(media, doc, path + ".source", kind == "image" ? ["bytes"] : ["bytes", "s3Location"]);
        if (media.ContainsKey("bytes")) _ = Convert.FromBase64String(Text(media["bytes"]));
        else
        {
            var location = Object(media["s3Location"]); var uri = Text(location["uri"]);
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "s3") throw new ArgumentException("媒体引用必须是原生 s3 URI。");
            if (location.ContainsKey("bucketOwner")) _ = Text(location["bucketOwner"]);
            body["s3Location"] = Pick(location, doc, path + ".source.s3Location", "uri", "bucketOwner");
        }
        target["source"] = body; return target;
    }

    /// <summary>普通工具定义与 Schema 原样复制；托管/缓存工具不能冒充普通函数。</summary>
    internal static JsonObject Tools(JsonObject source, ConversionDocument doc)
    {
        var path = "$.toolConfig"; var target = Pick(source, doc, path, "tools", "toolChoice"); JsonArray tools = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var node in Array(source["tools"]))
        {
            var tool = Object(node); if (!tool.ContainsKey("toolSpec") || tool.ContainsKey("cachePoint") || tool.ContainsKey("systemTool")) Reject(path + ".tools[]");
            Check(tool, doc, path + ".tools[]", "toolSpec"); var spec = Object(tool["toolSpec"]); var name = Text(spec["name"]);
            if (spec.ContainsKey("strict")) Reject(path + ".tools[].toolSpec.strict");
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) throw new ArgumentException("工具名称为空或重复。");
            if (spec.ContainsKey("description")) _ = Text(spec["description"]);
            var copied = Pick(spec, doc, path + ".tools[].toolSpec", "name", "description", "inputSchema");
            var schema = Object(spec["inputSchema"]); _ = Object(schema["json"]);
            copied["inputSchema"] = Pick(schema, doc, path + ".tools[].toolSpec.inputSchema", "json");
            tools.Add(new JsonObject { ["toolSpec"] = copied });
        }
        target["tools"] = tools;
        if (source.ContainsKey("toolChoice"))
        {
            var choice = Object(source["toolChoice"]);
            if (choice.Count != 1 || choice.First().Key is not ("auto" or "any" or "tool")) Reject(path + ".toolChoice");
            var key = choice.First().Key; var value = Object(choice[key]);
            if (key == "tool")
            {
                if (value.Count != 1 || !names.Contains(Text(value["name"]))) throw new ArgumentException("具名工具选择必须匹配本次定义。");
            }
            else if (value.Count != 0) Reject(path + ".toolChoice." + key);
        }
        return target;
    }
}
