using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.Conversion.Internal;

/// <summary>一个原生候选独立持有普通工具、文本与终态，不与其他候选合并。</summary>
internal sealed class CandidateStreamBranch
{
    private readonly SourceStream _source;
    private readonly TargetStream _target;
    private List<StreamChange> _pending = [];
    /// <summary>复用既有单候选数据转换，网络和终态批次由外层统一确认。</summary>
    internal CandidateStreamBranch(LlmProtocol from, LlmProtocol to, LlmConversionOptions options)
    {
        _source = new(from, options.MaxCandidateStreamInputBytes ?? 16 * 1024 * 1024,
            citationIndexUnit: options.OpenAiCitationIndexUnit, maximumAnnotations: options.MaxStreamAnnotations,
            maximumAnnotationBytes: options.MaxStreamAnnotationBytes);
        _target = new(to);
    }
    /// <summary>已验证的本候选内容及诊断。</summary>
    internal ConversionDocument Document => _source.Document;
    /// <summary>外层已经登记的诊断数量，避免重复计入累计诊断。</summary>
    internal int RecordedDiagnostics { get; set; }
    /// <summary>即时转发文本或已完整的原生 Gemini 函数调用。</summary>
    internal List<JsonObject> Push(JsonObject value) => _target.Push(Document, _source.Push(value));
    /// <summary>先验证所有候选都正常结束，再生成任何目标终态。</summary>
    internal void PrepareComplete()
    {
        _pending = _source.Complete();
        foreach (var block in Document.Messages.SelectMany(m => m.Blocks))
            if (block["type"]?.GetValue<string>() == "call")
            {
                var parsed = JsonNode.Parse(block["arguments"]!.GetValue<string>(), documentOptions: new() { AllowDuplicateProperties = false });
                if (parsed is not JsonObject) throw new InvalidDataException("函数参数必须是完整 JSON 对象。");
                CandidateStreamBudget.ValidateStrings(parsed);
            }
    }
    /// <summary>函数参数完整验证后输出；产生的终态仍由外层批次暂存。</summary>
    internal List<JsonObject> Finish()
    {
        var result = _target.Push(Document, _pending); result.AddRange(_target.Complete(Document)); return result;
    }
}
