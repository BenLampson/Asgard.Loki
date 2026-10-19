using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>只接受已验证的原生搜索字段，不把范围限制、离线模式或调用上限当成可删元数据。</summary>
internal static class HostedSearchSettings
{
    /// <summary>读取正式 Responses、经典 Claude 或 Gemini 文本 Google Search 的配置。</summary>
    internal static JsonObject Read(JsonObject tool, LlmProtocol source, string path)
    {
        var result = new JsonObject();
        if (source == LlmProtocol.Responses)
        {
            Known(tool, path, "type", "filters", "user_location", "search_context_size", "external_web_access");
            if (tool.ContainsKey("filters"))
            {
                var filters = Object(tool["filters"]); Known(filters, path + ".filters", "allowed_domains");
                if (filters.ContainsKey("allowed_domains")) result["allowed"] = Domains(filters["allowed_domains"], path + ".filters.allowed_domains");
            }
            if (tool.ContainsKey("search_context_size"))
            {
                if (Text(tool["search_context_size"]) is not ("low" or "medium" or "high")) throw new ArgumentException("无效的搜索上下文大小。");
                Copy(tool, "search_context_size", result, "context");
            }
            if (tool.ContainsKey("external_web_access")) { _ = Boolean(tool["external_web_access"]); Copy(tool, "external_web_access", result, "live"); }
            if (tool.ContainsKey("user_location")) result["location"] = Location(tool["user_location"], path + ".user_location");
        }
        else if (source == LlmProtocol.ClaudeMessages)
        {
            Known(tool, path, "type", "name", "max_uses", "allowed_domains", "blocked_domains", "user_location", "allowed_callers");
            if (Text(tool["name"]) != "web_search") Reject(path + ".name", "hosted_search_name_not_supported");
            if (tool.ContainsKey("allowed_domains") && tool.ContainsKey("blocked_domains")) Reject(path, "conflicting_search_filters");
            if (tool.ContainsKey("allowed_domains")) result["allowed"] = Domains(tool["allowed_domains"], path + ".allowed_domains");
            if (tool.ContainsKey("blocked_domains")) result["blocked"] = Domains(tool["blocked_domains"], path + ".blocked_domains");
            if (tool.ContainsKey("max_uses")) Copy(tool, "max_uses", result, "maxUses");
            if (tool.ContainsKey("user_location")) result["location"] = Location(tool["user_location"], path + ".user_location");
            if (tool.ContainsKey("allowed_callers"))
            {
                var callers = Array(tool["allowed_callers"]);
                if (callers.Count != 1 || Text(callers[0]) != "direct") Reject(path + ".allowed_callers", "programmatic_search_not_supported");
            }
        }
        else
        {
            Known(tool, path, "searchTypes", "timeRangeFilter");
            if (tool.ContainsKey("timeRangeFilter")) Reject(path + ".timeRangeFilter", "search_constraint_not_representable");
            if (tool.ContainsKey("searchTypes"))
            {
                var types = Object(tool["searchTypes"]); Known(types, path + ".searchTypes", "webSearch");
                if (types.Count != 1 || Object(types["webSearch"]).Count != 0) Reject(path + ".searchTypes", "search_type_not_supported");
            }
        }
        return result;
    }

    /// <summary>原生正式工具配置；不替换模型、补预算、增加搜索次数或放宽域名。</summary>
    internal static JsonObject Write(HostedSearchRequest search, LlmProtocol target, ConversionDocument doc)
    {
        var settings = search.Settings;
        if (settings.ContainsKey("maxUses")) Reject(search.Path + ".max_uses", "search_limit_not_representable");
        if (settings["live"] is not null && !Boolean(settings["live"]) && target != LlmProtocol.Responses)
            Reject(search.Path + ".external_web_access", "offline_search_not_representable");
        if (target == LlmProtocol.Gemini && settings.Count != 0)
        {
            foreach (var field in new[] { "allowed", "blocked", "location" })
                if (settings.ContainsKey(field)) Reject(search.Path, "search_constraint_not_representable");
        }
        if (settings.ContainsKey("context") && target != LlmProtocol.Responses)
            doc.Diagnostics.Add(new("search_context_size_not_representable", search.Path + ".search_context_size", "目标没有等价搜索上下文大小；原设置保留在 OriginalData。"));
        var tool = target switch
        {
            LlmProtocol.Responses => new JsonObject { ["type"] = "web_search" },
            LlmProtocol.ClaudeMessages => new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search" },
            _ => new JsonObject { ["googleSearch"] = new JsonObject() }
        };
        if (target == LlmProtocol.Responses)
        {
            if (settings.ContainsKey("blocked")) Reject(search.Path + ".blocked_domains", "search_constraint_not_representable");
            if (settings.TryGetPropertyValue("allowed", out var allowed)) tool["filters"] = new JsonObject { ["allowed_domains"] = allowed!.DeepClone() };
            Copy(settings, "context", tool, "search_context_size"); Copy(settings, "live", tool, "external_web_access");
        }
        else if (target == LlmProtocol.ClaudeMessages) Copy(settings, "allowed", tool, "allowed_domains");
        if (target != LlmProtocol.Gemini) Copy(settings, "location", tool, "user_location");
        return tool;
    }

    /// <summary>只转换双方都定义为域名的值；路径、通配符、IP 或空集合不得被删除以扩大范围。</summary>
    private static JsonArray Domains(JsonNode? node, string path)
    {
        var domains = Array(node);
        if (domains.Count == 0) Reject(path, "search_filter_not_representable");
        foreach (var entry in domains)
        {
            var domain = Text(entry);
            if (domain.Length == 0 || !domain.All(char.IsAscii) || Uri.CheckHostName(domain) != UriHostNameType.Dns)
                Reject(path, "search_filter_not_representable");
        }
        return domains.DeepClone().AsArray();
    }
    /// <summary>不猜测国家或时区；仅插入目标所需的已知 approximate 判别符。</summary>
    private static JsonObject Location(JsonNode? node, string path)
    {
        var location = Object(node); Known(location, path, "type", "city", "region", "country", "timezone");
        if (location.ContainsKey("type") && Text(location["type"]) != "approximate") Reject(path + ".type", "search_location_not_representable");
        if (!location.Any(p => p.Key != "type")) Reject(path, "search_location_not_representable");
        foreach (var entry in location.Where(p => p.Key != "type")) ArgumentException.ThrowIfNullOrWhiteSpace(Text(entry.Value));
        var result = location.DeepClone().AsObject(); result["type"] = "approximate"; return result;
    }
    /// <summary>未知搜索配置始终硬失败，不能借 AllowLoss 去掉范围/调用/数据访问约束。</summary>
    internal static void Known(JsonObject value, string path, params string[] fields)
    {
        foreach (var field in value) if (!fields.Contains(field.Key, StringComparer.Ordinal)) Reject(path + "." + field.Key, "unsupported_hosted_search_field");
    }
    /// <summary>诊断不记录原始查询、位置、密钥或域名。</summary>
    internal static void Reject(string path, string code) => throw new LlmConversionException([new(code, path, "当前原生目标不能保真表达该托管搜索设置。")]);
}
