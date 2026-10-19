using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asgard.Loki.LLM.OpenAI.Internal;

/// <summary>关联多条原生 lane 的提交、响应与终态；调用者在单个连接锁内访问。</summary>
internal sealed partial class ResponsesWebSocketState(OpenAiResponsesWebSocketOptions options)
{
    private readonly List<ResponsesWebSocketSubmission> _pending = [];
    private readonly Dictionary<string, ResponsesWebSocketResponseState> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    /// <summary>没有 lane 的连接级错误会终止连接，但原始错误事件先交给调用者。</summary>
    internal bool Fatal { get; private set; }

    /// <summary>在 socket 写入前保留提交身份；本地失败时可撤销尚未被服务器消费的记录。</summary>
    internal ResponsesWebSocketSubmission? Register(JsonObject message)
    {
        var data = JsonSerializer.SerializeToElement(message);
        var type = Required(data, "type");
        if (Fatal) throw new InvalidOperationException("WebSocket 已收到连接级错误。");
        var lane = Lane(data); var previous = Optional(data, type == "response.cancel" ? "response_id" : "previous_response_id");
        if (type == "response.steer")
        {
            previous = Required(data, "previous_response_id");
            if (!_responses.TryGetValue(previous, out var parent)) throw new ArgumentException("steer 必须引用当前连接已经收到的原生响应。");
            lane = parent.Lane;
        }
        else if (type == "response.cancel" && previous is not null && _responses.TryGetValue(previous, out var cancelled)) lane = cancelled.Lane;
        var eventId = Optional(data, "event_id");
        if (eventId is not null && _eventIds.Contains(eventId))
            throw new ArgumentException("连接内已关联的 event_id 不能重复。");
        if (eventId is not null && _eventIds.Count >= options.MaximumTrackedEventIds)
            throw new InvalidOperationException("连接显式事件身份数量超过上限。");
        if (_pending.Count >= options.MaximumPendingRequests) throw new InvalidOperationException("WebSocket 等待提交数量超过显式上限。");
        var request = new ResponsesWebSocketSubmission(type, lane, previous, eventId); _pending.Add(request);
        if (eventId is not null) _eventIds.Add(eventId); return request;
    }

    /// <summary>本地未发出的请求只撤销自己的登记，不修改其他并行生成。</summary>
    internal void Rollback(ResponsesWebSocketSubmission? request)
    {
        if (request is not null && _pending.Remove(request) && request.EventId is not null) _eventIds.Remove(request.EventId);
    }

    /// <summary>未知事件原样传出；只对已知响应与控制关联实施生命周期校验。</summary>
    internal void Accept(JsonElement data)
    {
        var type = Required(data, "type"); var lane = Lane(data);
        if (type == "error") { Error(data, lane); return; }
        if (type.StartsWith("response.steer.", StringComparison.Ordinal)) { Steering(data, lane, type); return; }
        if (type is "response.queued" or "response.created" or "response.in_progress" or "response.completed" or "response.failed" or "response.incomplete")
        {
            Lifecycle(data, lane, type); return;
        }
        if (!IsOutputEvent(type)) return;
        if (!_active.TryGetValue(lane, out var active)) throw new InvalidDataException("原生输出事件没有当前 lane 的活动响应，或已在终态之后。");
        var referenced = Optional(data, "response_id");
        if (referenced is not null && referenced != active) throw new InvalidDataException("输出事件引用了另一响应。");
        Sequence(data, _responses[active]);
    }

    /// <summary>socket 关闭不是生成终态；排队、活动响应和未落实 steering 均不得静默丢失。</summary>
    internal void Complete()
    {
        if (!Fatal && (_pending.Any(item => item.Type is "response.create" or "response.steer") || _responses.Values.Any(item => !item.Terminal)))
            throw new InvalidDataException("WebSocket 在全部请求的原生终态之前关闭，不会自动重连或重放。");
    }

    /// <summary>每个响应 ID 保持 lane 身份；queued 可以先于 created，但不能伪装成终态。</summary>
    private void Lifecycle(JsonElement data, string lane, string type)
    {
        if (!data.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("原生生命周期事件缺少 response 对象。");
        var id = Required(response, "id");
        if (!_responses.TryGetValue(id, out var state))
        {
            if (type is not ("response.created" or "response.queued")) throw new InvalidDataException("原生响应没有开始事件。");
            var prior = Optional(response, "previous_response_id");
            var request = _pending.FirstOrDefault(item => item.Type == "response.create" && item.Lane == lane);
            var steers = _pending.Where(item => item.Type == "response.steer" && item.Lane == lane && item.Previous == prior && item.SteerId is not null).ToArray();
            if (request is null && steers.Length == 0) throw new InvalidDataException("收到没有对应提交的原生响应。");
            if (_responses.Count >= options.MaximumTrackedResponses) throw new InvalidDataException("连接响应身份数量超过显式上限。");
            // 自动 steering 续接按原生 previous_response_id 关联，不能消耗同 lane 无关的排队请求。
            var consumed = steers.Length == 0 || request?.Previous == prior ? request : null;
            if (consumed is not null)
            {
                if (response.TryGetProperty("previous_response_id", out _) && consumed.Previous != prior)
                    throw new InvalidDataException("响应的 previous_response_id 与原始提交不一致。");
                _pending.Remove(consumed);
            }
            foreach (var steer in steers) _pending.Remove(steer);
            state = new(lane); _responses.Add(id, state);
        }
        if (state.Lane != lane || state.Terminal) throw new InvalidDataException("原生响应 lane 改变或在终态后继续发生生命周期事件。");
        var status = Optional(response, "status");
        var expected = type switch { "response.queued" => "queued", "response.in_progress" => "in_progress", "response.completed" => "completed", "response.failed" => "failed", "response.incomplete" => "incomplete", _ => null };
        if (expected is not null && status is not null && expected != status) throw new InvalidDataException("原生事件终态与 response.status 不一致。");
        Sequence(data, state);
        if (type == "response.created")
        {
            if (state.Created) throw new InvalidDataException("原生响应重复 created。"); state.Created = true;
        }
        if (type is "response.completed" or "response.failed" or "response.incomplete")
        {
            if (type == "response.completed" && !state.Created) throw new InvalidDataException("成功响应缺少 created 事件。");
            state.Terminal = true;
            if (_active.GetValueOrDefault(lane) == id) _active.Remove(lane);
            _pending.RemoveAll(item => item.Type == "response.cancel" && (item.Previous == id || item.Lane == lane && item.Previous is null));
        }
        else if (type != "response.queued" && status != "queued")
        {
            if (_active.TryGetValue(lane, out var active) && active != id) throw new InvalidDataException("同 lane 的生成响应发生重叠。");
            _active[lane] = id;
        }
    }

    /// <summary>控制错误不能错误结束仍在生成的响应；连接级错误则明确阻止继续使用。</summary>
    private void Error(JsonElement data, string lane)
    {
        var eventId = Optional(data, "event_id");
        if (eventId is null && data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object) eventId = Optional(error, "event_id");
        var matched = eventId is null ? null : _pending.SingleOrDefault(item => item.EventId == eventId);
        if (matched is not null) { _pending.Remove(matched); return; }
        // 已结束控制的迟到错误或未知 ID 都不能推断为另一个未关联创建的失败。
        if (eventId is not null) return;
        if (Optional(data, "response_id") is not null) return;
        // 原生无 lane 的错误在存在命名流时属于连接级，不能把它误当成某一流的成功完成。
        if (lane.Length == 0 && (_active.Keys.Any(key => key.Length != 0) || _pending.Any(item => item.Lane.Length != 0))) { Fatal = true; return; }
        var controls = _pending.Any(item => item.Lane == lane && item.Type != "response.create");
        if (controls) return;
        var queued = _pending.FirstOrDefault(item => item.Lane == lane && item.Type == "response.create");
        if (queued is not null) { _pending.Remove(queued); return; }
        // 活动生成的未关联错误保留为失败状态，仍要求其终态；无法推断错误等于成功结束。
    }

    /// <summary>一个 parent 可有多个被接受的 steering；真实续接 created 才是提交点。</summary>
    private void Steering(JsonElement data, string lane, string type)
    {
        if (type is not ("response.steer.accepted" or "response.steer.pending" or "response.steer.failed")) return;
        if (!data.TryGetProperty("steer", out var steer) || steer.ValueKind != JsonValueKind.Object) throw new InvalidDataException("steering 事件缺少关联对象。");
        var parent = Required(steer, "previous_response_id"); var id = type == "response.steer.failed" ? Optional(steer, "id") : Required(steer, "id");
        if (!_responses.TryGetValue(parent, out var response) || response.Lane != lane) throw new InvalidDataException("steering 事件引用另一 lane 或未知响应。");
        var request = type == "response.steer.accepted"
            ? _pending.FirstOrDefault(item => item.Type == "response.steer" && item.Previous == parent && item.SteerId is null)
            : _pending.FirstOrDefault(item => item.Type == "response.steer" && item.Previous == parent && item.SteerId == id);
        if (request is null && type == "response.steer.failed")
            request = _pending.FirstOrDefault(item => item.Type == "response.steer" && item.Previous == parent && item.SteerId is null);
        if (request is null) throw new InvalidDataException("steering 事件缺少对应提交。");
        if (type == "response.steer.accepted")
        {
            if (_pending.Any(item => item.SteerId == id)) throw new InvalidDataException("重复 steering ID。"); request.SteerId = id;
        }
        else if (type == "response.steer.failed") _pending.Remove(request);
    }
}
