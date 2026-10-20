using System.Text.Json;

namespace Asgard.Loki.Video.Tests;

/// <summary>只记录原生调用的内存目标，用于证明任务绑定与快照，不接触网络。</summary>
internal sealed class StubVideoAdapter(string id = "a") : IVideoTaskAdapter, IDisposable
{
    private int _submitted;
    /// <inheritdoc />
    public string Id { get; } = id;
    /// <summary>测试中显式改变任一完整目标分量。</summary>
    internal string Server { get; set; } = "https://server.invalid/v1/";
    /// <summary>实际上游模型。</summary>
    internal string Model { get; set; } = "actual-model";
    /// <summary>仅用于身份变化测试，不是凭据。</summary>
    internal string Credential { get; set; } = "test-credential";
    /// <summary>适配实现的稳定语义版本。</summary>
    internal string Implementation { get; set; } = "version-one";
    /// <inheritdoc />
    public string BindingIdentity => JsonSerializer.Serialize(new { Server, Model, Credential, Implementation });
    /// <summary>提交窗口。</summary>
    internal List<VideoTaskRequest> Requests { get; } = [];
    /// <summary>查询窗口。</summary>
    internal List<string> Queries { get; } = [];
    /// <summary>可控原生边界。</summary>
    internal Func<CancellationToken, Task>? BeforeSubmit { get; set; }
    /// <summary>可控查询返回 ID。</summary>
    internal string? ReturnedId { get; set; }
    /// <summary>借用适配器不由能力模块释放。</summary>
    internal int Disposals { get; private set; }
    /// <inheritdoc />
    public async Task<VideoTaskSnapshot> SubmitAsync(VideoTaskRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request); if (BeforeSubmit is not null) await BeforeSubmit(cancellationToken);
        return Snapshot($"task_{Id}_{++_submitted}", null, VideoTaskState.Submitted);
    }
    /// <inheritdoc />
    public Task<VideoTaskSnapshot> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Queries.Add(taskId);
        return Task.FromResult(Snapshot(ReturnedId ?? taskId, "running", VideoTaskState.Running));
    }
    /// <summary>完整原生 JSON 由结果对象独立拥有，不依赖临时文档。</summary>
    private static VideoTaskSnapshot Snapshot(string id, string? status, VideoTaskState state)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { id, status, future = new { zero = 0, optional = (string?)null, disabled = false } }));
        return new(id, status, state, document.RootElement);
    }
    /// <inheritdoc />
    public void Dispose() => Disposals++;
}
