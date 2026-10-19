using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Asgard.Loki.LLM.Conversion.Internal;

namespace Asgard.Loki.LLM.Conversion;

/// <summary>请求专属的不可变转换上下文；不持有网络、Session、工具执行器或全局缓存。</summary>
public sealed class LlmConversionContext
{
    /// <summary>复制映射和配置，使响应不能依赖调用者后续修改的集合。</summary>
    internal LlmConversionContext(LlmProtocol source, LlmProtocol target, LlmConversionOptions options,
        IEnumerable<ConvertedToolBinding>? tools = null, IEnumerable<string>? ordinaryNames = null, IEnumerable<string>? historicalIds = null)
    {
        SourceProtocol = source; TargetProtocol = target; Options = ConversionOptionsSnapshot.Capture(options);
        ToolBindings = new ReadOnlyDictionary<string, ConvertedToolBinding>((tools ?? []).ToDictionary(tool => tool.EncodedName, StringComparer.Ordinal));
        OrdinaryNames = new HashSet<string>(ordinaryNames ?? [], StringComparer.Ordinal);
        HistoricalIds = new HashSet<string>(historicalIds ?? [], StringComparer.Ordinal);
    }
    /// <summary>原始调用方协议。</summary>
    public LlmProtocol SourceProtocol { get; }
    /// <summary>转换后的实际上游协议。</summary>
    public LlmProtocol TargetProtocol { get; }
    /// <summary>本次请求中的 custom 工具映射数量，包含需要回放的历史工具。</summary>
    public int CustomToolCount => ToolBindings.Values.Count(tool => tool.Identity.IsCustom);
    /// <summary>需要还原 namespace 的映射数量，包含普通函数和 custom 历史。</summary>
    public int NamespacedToolCount => ToolBindings.Values.Count(tool => tool.Identity.Namespace is not null);
    /// <summary>本请求所有需还原的唯一工具身份总数，不把 namespaced custom 计算两次。</summary>
    public int MappedToolCount => ToolBindings.Count;
    /// <summary>请求时已经完成快照的选项，不允许响应临时切换损耗策略。</summary>
    internal LlmConversionOptions Options { get; }
    /// <summary>仅使用本请求内实际记录的编码映射。</summary>
    internal IReadOnlyDictionary<string, ConvertedToolBinding> ToolBindings { get; }
    /// <summary>普通工具不会因同名或编码相似而被伪装成 custom。</summary>
    internal IReadOnlySet<string> OrdinaryNames { get; }
    /// <summary>历史调用 ID 不能被本轮新调用复用，防止后续结果回填歧义。</summary>
    internal IReadOnlySet<string> HistoricalIds { get; }

    /// <summary>还原完整 Responses 工具项；不会执行调用或猜测损坏的参数。</summary>
    internal JsonObject Restore(JsonObject response)
    {
        if (MappedToolCount == 0) return response;
        var calls = 0;
        foreach (var node in ConversionJson.Array(response["output"]))
        {
            var item = ConversionJson.Object(node);
            if (item["type"]?.GetValue<string>() != "function_call") continue;
            if (++calls > Options.MaxToolConversionOutputCalls) ToolRequestCodec.Reject("$.output", "custom_tool_output_limit");
            RestoreItem(item, complete: true);
        }
        return response;
    }

    /// <summary>完整和流式路径共享身份校验，不猜测编码前缀，也不复用历史调用 ID。</summary>
    internal ConvertedToolBinding? ResolveTool(string name, string callId)
    {
        if (MappedToolCount == 0) return null;
        if (HistoricalIds.Contains(callId)) ToolRequestCodec.Reject("$.output[].call_id", "tool_call_id_reused");
        if (ToolBindings.TryGetValue(name, out var tool)) return tool;
        if (!OrdinaryNames.Contains(name)) ToolRequestCodec.Reject("$.output[].name", "tool_context_mismatch");
        return null;
    }

    /// <summary>开始项输入为空，完成项严格解包；源转换文档及其终态校验不被修改。</summary>
    internal void RestoreItem(JsonObject item, bool complete)
    {
        var tool = ResolveTool(ConversionJson.Text(item["name"]), ConversionJson.Text(item["call_id"]));
        if (tool is null) return;
        item["name"] = tool.Identity.Name;
        if (tool.Identity.Namespace is { } toolNamespace) item["namespace"] = toolNamespace;
        if (tool.Identity.IsCustom)
        {
            var input = complete ? CustomToolTextCodec.Decode(ConversionJson.Text(item["arguments"]), Options.MaxCustomToolInputBytes) : "";
            item["type"] = "custom_tool_call"; item["input"] = input; item.Remove("arguments");
        }
    }
}
