using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;
using static Asgard.Loki.LLM.Conversion.Internal.HostedSearchSettings;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>搜索配置独立于普通函数身份编码；只写入上游原生工具，不执行查询。</summary>
internal static class HostedSearchCodec
{
    /// <summary>先分离原生搜索声明，使 namespace/custom 转换不把托管工具伪装成函数。</summary>
    internal static HostedSearchRequest? Prepare(JsonObject request, LlmProtocol source, LlmProtocol target,
        LlmConversionOptions options, out JsonObject prepared)
    {
        prepared = request;
        if (source == LlmProtocol.ChatCompletions && request.ContainsKey("web_search_options"))
        {
            RequireTarget(target, options.HostedSearchTarget, "$.web_search_options");
            if (request.ContainsKey("tools") && Array(request["tools"]).Count != 0)
                Reject("$.tools", "chat_search_function_combination_not_supported");
            var chatSearch = new HostedSearchRequest(source, "$.web_search_options", Read(Object(request["web_search_options"]), source, "$.web_search_options")) { Choice = "search" };
            prepared = request.DeepClone().AsObject(); prepared.Remove("web_search_options");
            ReadChoice(prepared, chatSearch);
            return chatSearch;
        }
        if (request["tools"] is not JsonArray tools) return null;
        var remaining = new JsonArray(); HostedSearchRequest? search = null;
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = Object(tools[i]); var path = "$.tools[" + i + "]"; var type = tool["type"]?.GetValue<string>();
            var recognized = source == LlmProtocol.Responses && type is "web_search" or "web_search_2025_08_26" or "web_search_preview" or "web_search_preview_2025_03_11"
                || source == LlmProtocol.ClaudeMessages && type is "web_search_20250305" or "web_search_20260209" or "web_search_20260318"
                || source == LlmProtocol.Gemini && (tool.ContainsKey("googleSearch") || tool.ContainsKey("google_search") || tool.ContainsKey("googleSearchRetrieval"));
            if (!recognized) { remaining.Add(tool.DeepClone()); continue; }
            if (search is not null) Reject(path, "duplicate_hosted_search");
            RequireTarget(target, options.HostedSearchTarget, path);
            if (source == LlmProtocol.Responses && type is not ("web_search" or "web_search_2025_08_26")
                || source == LlmProtocol.ClaudeMessages && type != "web_search_20250305"
                || source == LlmProtocol.Gemini && (tool.ContainsKey("google_search") || tool.ContainsKey("googleSearchRetrieval")))
                Reject(path, "hosted_search_source_not_supported");
            var config = tool;
            if (source == LlmProtocol.Gemini)
            {
                Known(tool, path, "googleSearch", "functionDeclarations"); config = Object(tool["googleSearch"]);
                var rest = tool.DeepClone().AsObject(); rest.Remove("googleSearch"); if (rest.Count != 0) remaining.Add(rest); path += ".googleSearch";
            }
            search = new(source, path, Read(config, source, path));
        }
        if (search is null) return null;
        // 不把其他服务器工具顺带丢弃；该检查与常规未知字段损耗策略独立。
        foreach (var node in remaining)
        {
            var tool = Object(node); var type = tool["type"]?.GetValue<string>();
            if (source == LlmProtocol.Responses && type is not ("function" or "custom" or "namespace")
                || source == LlmProtocol.ClaudeMessages && type is not (null or "custom")
                || source == LlmProtocol.Gemini && (tool.Count != 1 || !tool.ContainsKey("functionDeclarations")))
                Reject("$.tools", "hosted_search_combination_not_supported");
        }
        prepared = request.DeepClone().AsObject();
        if (remaining.Count == 0) prepared.Remove("tools"); else prepared["tools"] = remaining;
        ReadChoice(prepared, search);
        return search;
    }

    /// <summary>目标由调用者明确声明，不通过模型名、后缀、在线目录或接口探测推断支持。</summary>
    private static void RequireTarget(LlmProtocol protocol, LlmHostedSearchTarget target, string path)
    {
        if (target == LlmHostedSearchTarget.None) Reject(path, "hosted_search_conversion_not_enabled");
        var matches = target switch
        {
            LlmHostedSearchTarget.ResponsesWebSearch => protocol == LlmProtocol.Responses,
            LlmHostedSearchTarget.ClaudeWebSearch20250305 => protocol == LlmProtocol.ClaudeMessages,
            LlmHostedSearchTarget.GeminiGoogleSearch => protocol == LlmProtocol.Gemini,
            LlmHostedSearchTarget.ChatWebSearch => protocol == LlmProtocol.ChatCompletions,
            _ => false
        };
        if (!matches) Reject(path, "hosted_search_target_mismatch");
    }

    /// <summary>普通函数仍由现有写入器处理，搜索声明和选择保留自己的原生类型。</summary>
    internal static void Write(JsonObject target, HostedSearchRequest? search, LlmProtocol protocol, LlmConversionOptions options, ConversionDocument doc)
    {
        if (search is null) return;
        if (doc.Tools.Count != 0 && !options.AllowHostedSearchWithFunctionTools) Reject("$.tools", "hosted_search_function_combination_not_declared");
        if ((protocol == LlmProtocol.ClaudeMessages || search.Source == LlmProtocol.ClaudeMessages) && doc.Tools.Any(t => Text(t!["name"]) == "web_search"))
            Reject("$.tools", "hosted_search_name_collision");
        var tool = HostedSearchSettings.Write(search, protocol, doc);
        if (protocol == LlmProtocol.ChatCompletions)
        {
            if (doc.Tools.Count != 0) Reject("$.tools", "chat_search_function_combination_not_supported");
            if (search.Choice is not ("search" or "required")) Reject("$.tool_choice", "hosted_search_choice_not_representable");
            target["web_search_options"] = tool;
            return; // Chat 搜索模型先搜索；不能将 auto/none 改为这一执行语义。
        }
        if (target["tools"] is not JsonArray tools) { tools = []; target["tools"] = tools; }
        tools.Add(tool);
        if (search.Choice is null) return;
        if (protocol == LlmProtocol.Gemini)
        {
            if (search.Choice != "auto") Reject("$.tool_choice", "hosted_search_choice_not_representable");
            return; // functionCallingConfig 控制函数，不能拿它强制/关闭 Google Search。
        }
        if (search.Choice == "function") return;
        target["tool_choice"] = protocol == LlmProtocol.Responses
            ? search.Choice == "search" ? new JsonObject { ["type"] = "web_search" } : JsonValue.Create(search.Choice)
            : new JsonObject { ["type"] = search.Choice switch { "search" => "tool", "required" => "any", _ => search.Choice } };
        if (protocol == LlmProtocol.ClaudeMessages && search.Choice == "search") target["tool_choice"]!["name"] = "web_search";
    }

    /// <summary>只转换已验证的选择语义；函数配置不控制搜索，显式并行控制也不冒充等价。</summary>
    private static void ReadChoice(JsonObject request, HostedSearchRequest search)
    {
        if (request.ContainsKey("parallel_tool_calls")) Reject("$.parallel_tool_calls", "hosted_search_parallel_not_supported");
        if (search.Source == LlmProtocol.ChatCompletions)
        {
            if (request.ContainsKey("tool_choice")) Reject("$.tool_choice", "hosted_search_choice_not_representable");
            return; // 原生函数选择器不控制 web_search_options；不猜测组合语义。
        }
        if (search.Source == LlmProtocol.Gemini)
        {
            if (!request.ContainsKey("toolConfig")) return;
            var config = Object(request["toolConfig"]); Known(config, "$.toolConfig", "functionCallingConfig");
            var choice = Object(config["functionCallingConfig"]); Known(choice, "$.toolConfig.functionCallingConfig", "mode");
            if (Text(choice["mode"]) != "AUTO") Reject("$.toolConfig.functionCallingConfig", "hosted_search_choice_not_representable");
            search.Choice = "auto"; request.Remove("toolConfig"); return;
        }
        if (!request.ContainsKey("tool_choice")) return;
        if (request["tool_choice"] is JsonValue mode && search.Source == LlmProtocol.Responses)
        {
            var value = Text(mode); if (value is not ("auto" or "required" or "none")) Reject("$.tool_choice", "hosted_search_choice_not_representable");
            search.Choice = value;
        }
        else
        {
            var choice = Object(request["tool_choice"]); var type = Text(choice["type"]);
            if (search.Source == LlmProtocol.Responses && type == "web_search") { Known(choice, "$.tool_choice", "type"); search.Choice = "search"; }
            else if (search.Source == LlmProtocol.ClaudeMessages)
            {
                Known(choice, "$.tool_choice", type == "tool" ? ["type", "name"] : ["type"]);
                if (type == "tool" && Text(choice["name"]) == "web_search") search.Choice = "search";
                else if (type is "auto" or "any" or "none") search.Choice = type == "any" ? "required" : type;
                else if (type == "tool") { search.Choice = "function"; return; }
                else Reject("$.tool_choice", "hosted_search_choice_not_representable");
            }
            else if (type is "function" or "custom") { search.Choice = "function"; return; }
            else Reject("$.tool_choice", "hosted_search_choice_not_representable");
        }
        request.Remove("tool_choice");
    }
}
