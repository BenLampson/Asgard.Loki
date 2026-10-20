using System.Text.Json;
using Asgard.Loki.Audio.Internal;

namespace Asgard.Loki.Audio;

/// <summary>显式音频原生任务提交、状态与结果查询；没有后台轮询或重试。</summary>
public sealed class AudioTaskClient
{
    private readonly Dictionary<string, AudioTaskRouteState> _routes;
    /// <summary>每个客户端独立持有首次选择序号。</summary>
    internal AudioTaskClient(AudioTaskConfiguration configuration) => _routes = configuration.Routes.ToDictionary(
        pair => pair.Key, pair => new AudioTaskRouteState(pair.Value), StringComparer.Ordinal);
    /// <summary>提交一次原生操作，保存完整目标和操作绑定。</summary>
    public Task<AudioTask> SubmitAsync(AudioTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        var snapshot = request.Snapshot(); var target = Route(snapshot.Model).Select(); target.Validate();
        return SubmitCoreAsync(snapshot, target, cancellationToken);
    }
    /// <summary>调用完成后验证配置与操作，防止结果错误关联。</summary>
    private static async Task<AudioTask> SubmitCoreAsync(AudioTaskRequest request, AudioTaskTarget target, CancellationToken cancellationToken)
    {
        var result = await target.Adapter.SubmitAsync(request, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("音频适配器未返回原生任务。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (result.Operation != request.Operation) throw new InvalidDataException("音频提交返回了其他操作的任务。");
        return new(new(request.Model, target.Id, target.Fingerprint(request.Operation), result.TaskId, request.Operation), result);
    }
    /// <summary>只向引用绑定的原目标查询一次状态。</summary>
    public Task<AudioTask> GetAsync(AudioTaskReference reference, CancellationToken cancellationToken = default)
    {
        var target = Resolve(reference, cancellationToken); return GetCoreAsync(reference, target, cancellationToken);
    }
    /// <summary>状态窗口必须同时匹配任务 ID 和操作。</summary>
    private static async Task<AudioTask> GetCoreAsync(AudioTaskReference reference, AudioTaskTarget target, CancellationToken cancellationToken)
    {
        var result = await target.Adapter.GetAsync(new(reference.TaskId, reference.Operation), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("音频适配器未返回原生任务。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested(); Match(reference, result);
        return new(reference, result);
    }
    /// <summary>显式调用原生批量接口；缺失引用单独报告，不补造状态，也不退化为逐个查询。</summary>
    public Task<AudioTaskBatchResult> GetBatchAsync(IReadOnlyList<AudioTaskReference> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references); cancellationToken.ThrowIfCancellationRequested();
        var copy = references.ToArray(); ArgumentOutOfRangeException.ThrowIfZero(copy.Length);
        var target = Resolve(copy[0], cancellationToken);
        foreach (var reference in copy)
            if (reference is null || reference.Model != copy[0].Model || !ReferenceEquals(Resolve(reference, cancellationToken), target))
                throw new ArgumentException("音频批量查询必须绑定同一公共路由和完整目标。");
        if (copy.Select(reference => reference.TaskId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("音频批量查询任务 ID 重复。");
        var batch = target.Adapter as IAudioBatchTaskAdapter ?? throw new NotSupportedException("原目标未提供原生批量音频查询。");
        return BatchCoreAsync(copy, target, batch, cancellationToken);
    }
    /// <summary>原生返回顺序保留在 Data，关联结果按请求顺序输出并列出缺失引用。</summary>
    private static async Task<AudioTaskBatchResult> BatchCoreAsync(AudioTaskReference[] references, AudioTaskTarget target,
        IAudioBatchTaskAdapter adapter, CancellationToken cancellationToken)
    {
        var queries = references.Select(reference => new AudioTaskQuery(reference.TaskId, reference.Operation)).ToArray();
        var result = await adapter.GetBatchAsync(Array.AsReadOnly(queries), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("音频适配器未返回批量结果。");
        target.Validate(); cancellationToken.ThrowIfCancellationRequested();
        var expected = references.ToDictionary(reference => reference.TaskId, StringComparer.Ordinal);
        var found = new Dictionary<string, AudioTaskSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in result.Tasks)
        {
            if (!expected.TryGetValue(snapshot.TaskId, out var reference)) throw new InvalidDataException("音频批量查询返回了其他任务。");
            Match(reference, snapshot);
            if (!found.TryAdd(snapshot.TaskId, snapshot)) throw new InvalidDataException("音频批量查询重复返回任务。");
        }
        return new(references.Where(reference => found.ContainsKey(reference.TaskId)).Select(reference => new AudioTask(reference, found[reference.TaskId])).ToArray(),
            references.Where(reference => !found.ContainsKey(reference.TaskId)).ToArray(), result.Data);
    }
    /// <summary>显式读取原生结果接口；结果未提供状态时不猜测终态或生成媒体。</summary>
    public Task<AudioTaskResult> GetResultAsync(AudioTaskReference reference, CancellationToken cancellationToken = default)
    {
        var target = Resolve(reference, cancellationToken);
        var adapter = target.Adapter as IAudioTaskResultAdapter ?? throw new NotSupportedException("原目标未提供独立音频结果接口。");
        return ResultCoreAsync(reference, target, adapter, cancellationToken);
    }
    /// <summary>验证完整原生对象并复制文档所有权；供应商负责原生任务回显的关联校验。</summary>
    private static async Task<AudioTaskResult> ResultCoreAsync(AudioTaskReference reference, AudioTaskTarget target,
        IAudioTaskResultAdapter adapter, CancellationToken cancellationToken)
    {
        var data = await adapter.GetResultAsync(new(reference.TaskId, reference.Operation), cancellationToken).ConfigureAwait(false);
        target.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (data.ValueKind != JsonValueKind.Object) throw new InvalidDataException("音频原生结果必须是完整 JSON 对象。");
        AudioTaskJson.Validate(data); return new(reference, data);
    }
    /// <summary>引用中的操作也是绑定的一部分，不能查询时切换。</summary>
    private AudioTaskTarget Resolve(AudioTaskReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        foreach (var value in new[] { reference.Model, reference.TargetId, reference.Fingerprint, reference.TaskId, reference.Operation })
        { ArgumentException.ThrowIfNullOrWhiteSpace(value); AudioTaskJson.Text(value); }
        cancellationToken.ThrowIfCancellationRequested(); return Route(reference.Model).Resolve(reference);
    }
    /// <summary>拒绝混用 ID 或操作，即使原生状态相同。</summary>
    private static void Match(AudioTaskReference reference, AudioTaskSnapshot snapshot)
    {
        if (snapshot.TaskId != reference.TaskId || snapshot.Operation != reference.Operation)
            throw new InvalidDataException("音频查询返回了其他任务或操作的结果。");
    }
    /// <summary>只解析显式公共路由。</summary>
    private AudioTaskRouteState Route(string model) => _routes.TryGetValue(model, out var route)
        ? route : throw new InvalidOperationException("音频任务路由未配置。");
}
