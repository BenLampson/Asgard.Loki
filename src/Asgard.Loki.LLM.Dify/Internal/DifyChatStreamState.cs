using System.Net;
using System.Text;
using System.Text.Json;
using Asgard.Loki.Infrastructure;

namespace Asgard.Loki.LLM.Dify.Internal;

/// <summary>调用级原生应用生命周期与独立关联域，不执行工作流、工具、TTS 或恢复循环。</summary>
internal sealed class DifyChatStreamState(DifyChatTarget target, string? expectedConversation)
{
    private string? _task;
    private string? _message;
    private string? _conversation;
    private string? _run;
    private string? _workflow;
    private bool _started;
    private bool _messageEnd;
    private bool _workflowEnd;
    private bool _paused;
    /// <summary>首个真实生成结束阶段起缓冲完整尾流，防止提前报告局部/全局终态。</summary>
    internal bool HoldTail => _messageEnd || _workflowEnd || _paused;
    /// <summary>真实根 error 优先保留原始字节，嵌套业务 event/type 不参与生命周期。</summary>
    internal DifyChatEvent Push(SseFrame frame, HttpStatusCode status, string? contentType)
    {
        var data = DifyJson.Parse(Encoding.UTF8.GetBytes(frame.Data));
        if (DifyChatResponse.Error(data)) throw new LlmUpstreamException("Dify 原生 SSE 错误。", status, Encoding.UTF8.GetBytes(frame.Data), contentType);
        var kind = DifyChatResponse.Text(data, "event");
        var workflow = kind is "workflow_started" or "workflow_finished" or "workflow_paused";
        var node = kind is "node_started" or "node_finished" or "node_retry" or "iteration_started" or "iteration_next" or "iteration_completed" or "loop_started" or "loop_next" or "loop_completed" or "text_chunk" or "text_replace" or "reasoning_chunk" or "agent_log" or "human_input_required" or "human_input_form_filled" or "human_input_form_timeout";
        var generation = kind is "message" or "agent_message" or "agent_thought" or "message_file" or "message_replace";
        var audio = kind is "tts_message" or "tts_message_end";
        var known = workflow || node || generation || audio || kind == "message_end";
        if (!known) return Event(frame, data, false);
        Bind(ref _task, DifyChatResponse.Text(data, "task_id")); Bind(ref _message, DifyChatResponse.Text(data, "message_id"));
        var conversation = DifyChatResponse.Text(data, "conversation_id"); Bind(ref _conversation, conversation);
        if (!string.IsNullOrEmpty(expectedConversation) && conversation != expectedConversation) throw new InvalidDataException("Dify SSE 属于另一会话。");
        if (workflow || node || data.TryGetProperty("workflow_run_id", out _))
        {
            if (target.Options.Profile != DifyChatProfile.Chatflow) throw new InvalidDataException("Dify 工作流事件与显式应用类别不匹配。");
            if (workflow) Bind(ref _run, DifyChatResponse.Text(data, "workflow_run_id"));
            else if (data.TryGetProperty("workflow_run_id", out _)) Bind(ref _run, DifyChatResponse.Text(data, "workflow_run_id"));
        }
        if (node && !_started) throw new InvalidDataException("Dify 工作流节点事件缺少原生 workflow_started。");
        if (generation && HoldTail || node && (_workflowEnd || _paused)) throw new InvalidDataException("Dify 原生结束阶段后出现晚到生成事件。");
        if (kind is "message" or "agent_message" or "message_replace")
            if (!data.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String) throw new InvalidDataException("Dify 原生文本事件缺少字符串 answer。");
        var terminal = false;
        switch (kind)
        {
            case "workflow_started":
                if (_started || HoldTail) throw new InvalidDataException("Dify 工作流重复开始或开始晚于结束。");
                _started = true; WorkflowData(data, false); break;
            case "message_end":
                if (_messageEnd || _paused) throw new InvalidDataException("Dify 消息重复结束或结束晚于暂停。");
                _messageEnd = true;
                // 原生输入审核/注释直答不启动工作流；只在完全没有运行身份时采用该实际 message_end。
                terminal = target.Options.Profile != DifyChatProfile.Chatflow || !_started && _run is null; break;
            case "workflow_finished":
                if (!_started || _workflowEnd || _paused) throw new InvalidDataException("Dify 工作流缺少开始、重复结束或结束晚于暂停。");
                var outcome = WorkflowData(data, true);
                if (outcome is "running" or "paused" or "pending") throw new InvalidDataException("Dify workflow_finished 不是原生结束状态。");
                if (outcome is "succeeded" or "partial-succeeded" && !_messageEnd) throw new InvalidDataException("Dify 成功工作流缺少先行 message_end。");
                _workflowEnd = true; terminal = true; break;
            case "workflow_paused":
                if (!_started || _workflowEnd || _paused || _messageEnd) throw new InvalidDataException("Dify 暂停缺少开始或与结束冲突。");
                if (WorkflowData(data, true) != "paused") throw new InvalidDataException("Dify workflow_paused 缺少原生 paused 状态。");
                _paused = true; terminal = true; break;
        }
        return Event(frame, data, terminal);
    }
    /// <summary>只检查工作流自身 ID 域；节点、文件和思考的嵌套 id 绝不拿来比较消息或运行 ID。</summary>
    private string? WorkflowData(JsonElement root, bool outcome)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Dify 工作流缺少原生 data 对象。");
        if (data.TryGetProperty("workflow_id", out _)) Bind(ref _workflow, DifyChatResponse.Text(data, "workflow_id"));
        if (data.TryGetProperty("id", out _) && DifyChatResponse.Text(data, "id") != _run) throw new InvalidDataException("Dify 工作流原生运行关联不匹配。");
        if (data.TryGetProperty("workflow_run_id", out _) && DifyChatResponse.Text(data, "workflow_run_id") != _run) throw new InvalidDataException("Dify 暂停原生运行关联不匹配。");
        return outcome ? DifyChatResponse.Text(data, "status") : null;
    }
    /// <summary>每个原生域只绑定自身，事件 root id 始终保持独立，不按供应商索引分配表。</summary>
    private static void Bind(ref string? current, string value)
    { if (current is not null && current != value) throw new InvalidDataException("Dify SSE 原生关联域发生变化。"); current = value; }
    /// <summary>保持原始传输和 JSON 所有权，不追加 DONE/stop/回答或用量。</summary>
    private DifyChatEvent Event(SseFrame frame, JsonElement data, bool terminal) => new(frame.Data, frame.EventName, frame.EventId, frame.RetryMilliseconds, data, target.Metadata, terminal);
    /// <summary>已启动/有关联运行的 Chatflow 只收到 message_end 不能完成；原生无运行直答及失败/停止/暂停保持原文。</summary>
    internal void Complete()
    {
        if (_task is null || _message is null || _conversation is null || (target.Options.Profile == DifyChatProfile.Chatflow ? !_workflowEnd && !_paused && !(_messageEnd && !_started && _run is null) : !_messageEnd))
            throw new InvalidDataException("Dify SSE 缺少所选原生应用的完整终态。");
    }
}
