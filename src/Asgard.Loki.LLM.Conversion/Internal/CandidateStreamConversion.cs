using System.Text.Json;
using System.Text.Json.Nodes;
using static Asgard.Loki.LLM.Conversion.Internal.ConversionJson;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>多候选只分离协议状态，不调度目标、执行工具或托管后台任务。</summary>
internal sealed class CandidateStreamConversion
{
    private readonly LlmProtocol _from;
    private readonly LlmProtocol _to;
    private readonly LlmConversionOptions _options;
    private readonly SourceStream _header;
    private readonly CandidateStreamBudget _budget;
    private readonly Dictionary<int, CandidateStreamBranch> _branches = [];
    private readonly ConversionDocument _diagnostics = new();
    private int _headerRecorded;
    private string? _id;
    private string _model = "";
    private long _created;
    private long? _nativeCreated;
    /// <summary>仅对 Chat/Gemini 一对原生逻辑流启用。</summary>
    internal CandidateStreamConversion(LlmProtocol from, LlmProtocol to, LlmConversionOptions options)
    {
        _from = from; _to = to; _options = options; _header = new(from); _budget = new(options);
    }
    /// <summary>累计诊断不可变快照，候选路径明确使用逻辑原生索引。</summary>
    internal IReadOnlyList<LlmConversionDiagnostic> Diagnostics => System.Array.AsReadOnly(_diagnostics.Diagnostics.ToArray());
    /// <summary>两个方向都必须由调用者明确开启，其他协议继续使用已有状态机。</summary>
    internal static bool Applies(LlmProtocol from, LlmProtocol to) => from == LlmProtocol.ChatCompletions && to == LlmProtocol.Gemini
        || from == LlmProtocol.Gemini && to == LlmProtocol.ChatCompletions;
    /// <summary>每帧先验证公共信封和原始候选身份，然后保持帧内顺序返回增量。</summary>
    internal List<JsonObject> Push(JsonObject source)
    {
        _budget.Input(source);
        if (_from == LlmProtocol.Gemini && source["promptFeedback"] is JsonObject feedback && feedback["blockReason"] is not null
            && Text(feedback["blockReason"]) is { Length: > 0 } reason && reason != "BLOCK_REASON_UNSPECIFIED")
            throw new LlmConversionException([new("upstream_stream_error", "$event.promptFeedback.blockReason", "上游阻断不能转换为成功终态。")]);
        var sourceKey = _from == LlmProtocol.Gemini ? "candidates" : "choices";
        var header = source.DeepClone().AsObject(); header[sourceKey] = new JsonArray();
        _header.Push(header); RecordHeader(); Enforce();
        if (_from == LlmProtocol.ChatCompletions && source.ContainsKey("created"))
        {
            var created = Number(source["created"]);
            if (created < 0 || _nativeCreated is { } old && old != created) throw new InvalidDataException("Chat 流创建时间必须保持一致。");
            _nativeCreated = created;
        }
        var candidates = source.ContainsKey(sourceKey) ? Array(source[sourceKey]) : _from == LlmProtocol.Gemini
            ? new JsonArray() : throw new InvalidDataException("Chat 原生事件缺少 choices 数组。");
        var frameIndices = new HashSet<int>();
        foreach (var node in candidates)
            if (!frameIndices.Add(Index(Object(node)))) throw new InvalidDataException("同一原生事件的候选索引不能重复。");
        List<JsonObject> output = [];
        foreach (var node in candidates)
        {
            var candidate = Object(node); var index = Index(candidate);
            if (!_branches.TryGetValue(index, out var branch))
            {
                if (_branches.Count >= (_options.MaxStreamCandidates ?? 128)) CandidateStreamBudget.Reject("stream_candidate_limit");
                branch = new(_from, _to, _options); _branches.Add(index, branch);
            }
            if (_id is null)
            {
                _id = _header.Document.Id.Length == 0 ? "loki_" + Guid.NewGuid().ToString("N") : _header.Document.Id;
                _model = _header.Document.Model; _created = _header.Document.Created;
            }
            var part = candidate.DeepClone().AsObject(); part["index"] = 0;
            var sliced = Envelope(_from); sliced[sourceKey] = new JsonArray(part);
            List<JsonObject> items;
            try { items = branch.Push(sliced); }
            catch (LlmConversionException error) { throw Scope(error, index); }
            Record(branch, index); Enforce(); Rewrite(items, index); output.AddRange(items);
        }
        _budget.Output(output, source); return output;
    }
    /// <summary>所有源候选通过验证后才构建终态批次，并在批次末尾仅输出一次共享用量。</summary>
    internal List<JsonObject> Complete(JsonObject last)
    {
        if (_branches.Count == 0) throw new InvalidDataException("源流没有候选，不能生成成功终态。");
        foreach (var entry in _branches)
        {
            try { entry.Value.PrepareComplete(); }
            catch (LlmConversionException error) { throw Scope(error, entry.Key); }
            Record(entry.Value, entry.Key);
        }
        Enforce(); List<JsonObject> output = [];
        foreach (var entry in _branches)
        {
            List<JsonObject> items;
            try { items = entry.Value.Finish(); }
            catch (LlmConversionException error) { throw Scope(error, entry.Key); }
            Record(entry.Value, entry.Key); Enforce(); Rewrite(items, entry.Key); output.AddRange(items);
        }
        var usage = UsageMapping.Write(_header.Document, _to); RecordHeader(); Enforce();
        if (usage.Count != 0)
        {
            var final = Envelope(_to); final[_to == LlmProtocol.Gemini ? "candidates" : "choices"] = new JsonArray();
            final[_to == LlmProtocol.Gemini ? "usageMetadata" : "usage"] = usage; output.Add(final);
        }
        _budget.Output(output, last); return output;
    }
    /// <summary>公开目标身份在第一次候选到达时固定；真实源身份始终由公共信封独立校验。</summary>
    private JsonObject Envelope(LlmProtocol protocol) => protocol == LlmProtocol.Gemini
        ? new() { ["responseId"] = _id, ["modelVersion"] = _model }
        : new() { ["id"] = _id, ["object"] = "chat.completion.chunk", ["created"] = _created, ["model"] = _model };
    /// <summary>内部复用零号候选，公开结果恢复原生索引；用量只由公共尾帧承载。</summary>
    private void Rewrite(List<JsonObject> items, int index)
    {
        foreach (var item in items)
        {
            foreach (var candidate in Array(item[_to == LlmProtocol.Gemini ? "candidates" : "choices"])) Object(candidate)["index"] = index;
            item.Remove(_to == LlmProtocol.Gemini ? "usageMetadata" : "usage");
        }
    }
    /// <summary>原生 Gemini 省略 int32 为零；Chat 及显式非法索引不能猜测。</summary>
    private int Index(JsonObject candidate)
    {
        if (_from == LlmProtocol.Gemini && !candidate.ContainsKey("index")) return 0;
        var element = JsonSerializer.SerializeToElement(candidate["index"]);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 0)
            throw new InvalidDataException("流式候选索引必须为非负 int32 数值。");
        return value;
    }
    /// <summary>公共未知字段和源用量明细只登记一次，不按候选数重复。</summary>
    private void RecordHeader()
    {
        _diagnostics.Diagnostics.AddRange(_header.Document.Diagnostics.Skip(_headerRecorded));
        _headerRecorded = _header.Document.Diagnostics.Count;
    }
    /// <summary>逻辑候选索引明确区分于源 JSON 数组位置，原始事件由外层完整保留。</summary>
    private void Record(CandidateStreamBranch branch, int index)
    {
        _diagnostics.Diagnostics.AddRange(branch.Document.Diagnostics.Skip(branch.RecordedDiagnostics).Select(d => Scoped(d, index)));
        branch.RecordedDiagnostics = branch.Document.Diagnostics.Count;
    }
    /// <summary>候选级异常不丢失其原生关联身份。</summary>
    private static LlmConversionException Scope(LlmConversionException error, int index) => new(error.Diagnostics.Select(d => Scoped(d, index)));
    /// <summary>路径前缀是逻辑关联上下文，不把稀疏原生索引伪装成数组下标。</summary>
    private static LlmConversionDiagnostic Scoped(LlmConversionDiagnostic item, int index)
        => item with { Path = "$candidate[" + index + "]." + item.Path.TrimStart('$', '.') };
    /// <summary>损耗策略与共享诊断上限在交付每批结果前统一执行。</summary>
    private void Enforce() { _budget.Diagnostics(_diagnostics.Diagnostics.Count); LlmProtocolConverter.Enforce(_diagnostics, _options); }
}
