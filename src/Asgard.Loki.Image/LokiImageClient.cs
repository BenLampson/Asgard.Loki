namespace Asgard.Loki.Image;

/// <summary>根据公开模型选择完整上游并交给其 Handler，不进行重试、故障转移或自动下载。</summary>
public sealed class LokiImageClient
{
    private readonly Dictionary<string, LokiImageModel> _models;
    private readonly Dictionary<string, ILokiImageProviderSelector> _selectors;
    private readonly IHttpClientFactory _httpFactory;
    private readonly LokiImageContentReader _content;

    internal LokiImageClient(IReadOnlyList<LokiImageModel> models, IHttpClientFactory factory, LokiImageContentOptions content)
    {
        _models = models.ToDictionary(model => model.Name, StringComparer.Ordinal);
        _selectors = models.ToDictionary(model => model.Name, model => model.Selector ?? new RoundRobinImageProviderSelector(), StringComparer.Ordinal);
        _httpFactory = factory; _content = new(factory, content);
    }

    /// <summary>冻结输入、只选择一次完整目标，再执行目标的统一参数校验和生成。</summary>
    public Task<LokiImageGenerationResult> GenerateAsync(LokiImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Internal.LokiImageSnapshots.Request(request);
        var model = Model(snapshot.Model);
        var candidates = Array.AsReadOnly(model.Providers.Select(LokiImageFactory.Snapshot).ToArray());
        var index = _selectors[model.Name].Select(model.Name, candidates);
        if ((uint)index >= (uint)candidates.Count) throw new InvalidOperationException("图片选择器返回了候选集合外的索引。");
        var invocation = new LokiImageInvocation(LokiImageFactory.Snapshot(model.Providers[index]), _httpFactory);
        return ExecuteAsync(invocation, snapshot.Model, () => invocation.Provider.Handler!.GenerateAsync(invocation, snapshot, cancellationToken), cancellationToken);
    }

    /// <summary>原生请求必须明确指定目标，绕过统一参数表但保留上游自己的原生校验。</summary>
    public Task<LokiImageGenerationResult> GenerateNativeAsync(LokiNativeImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Internal.LokiImageSnapshots.Native(request);
        var provider = Model(snapshot.Model).Providers.SingleOrDefault(provider => provider.Id == snapshot.ProviderId)
            ?? throw new InvalidOperationException("当前公开图片模型没有指定的原生上游。");
        var invocation = new LokiImageInvocation(LokiImageFactory.Snapshot(provider), _httpFactory);
        return ExecuteAsync(invocation, snapshot.Model, () => invocation.Provider.Handler!.GenerateNativeAsync(invocation, snapshot, cancellationToken), cancellationToken);
    }

    /// <summary>显式解码或下载一张图片；失败不重新生成，原始生成结果仍由调用者保留。</summary>
    public Task<LokiImageContent> GetImageContentAsync(LokiGeneratedImage image, CancellationToken cancellationToken = default)
        => _content.ReadAsync(image, cancellationToken);

    /// <summary>只查询任务原来绑定的完整上游，配置变更、移除或任务身份不符均失败。</summary>
    public Task<LokiImageGenerationResult> GetAsync(LokiImageTaskReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Model); ArgumentException.ThrowIfNullOrWhiteSpace(reference.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.TaskId); ArgumentException.ThrowIfNullOrWhiteSpace(reference.TargetFingerprint);
        Internal.LokiImageSnapshots.Text(reference.TaskId);
        var provider = Model(reference.Model).Providers.SingleOrDefault(provider => provider.Id == reference.ProviderId)
            ?? throw new InvalidOperationException("图片任务原上游已移除。");
        if (Internal.LokiImageBinding.Fingerprint(reference.Model, provider) != reference.TargetFingerprint)
            throw new ArgumentException("图片任务的完整目标配置或 Handler 已变更。", nameof(reference));
        if (provider.Handler is not ILokiImageTaskHandler handler) throw new NotSupportedException("此图片 Handler 不提供原生任务查询。");
        var invocation = new LokiImageInvocation(LokiImageFactory.Snapshot(provider), _httpFactory);
        var snapshot = reference with { NativeReference = reference.NativeReference?.Clone() };
        return ExecuteAsync(invocation, reference.Model, () => handler.GetAsync(invocation, snapshot, cancellationToken), cancellationToken, reference.TaskId);
    }

    private LokiImageModel Model(string name) => _models.TryGetValue(name, out var model)
        ? model : throw new InvalidOperationException($"公开图片模型 {name} 没有注册。");

    private static async Task<LokiImageGenerationResult> ExecuteAsync(LokiImageInvocation invocation, string model,
        Func<Task<LokiImageGenerationResult>> call, CancellationToken token, string? expectedTaskId = null)
    {
        var fingerprint = Internal.LokiImageBinding.Fingerprint(model, invocation.Provider);
        var result = await call().ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(result);
        if (Internal.LokiImageBinding.Fingerprint(model, invocation.Provider) != fingerprint)
            throw new InvalidOperationException("图片 Handler 在调用期间修改了完整目标配置或协议身份。");
        if (result.Model != model || result.UpstreamModel != invocation.Provider.UpstreamModel
            || result.ProviderId != invocation.Provider.Id)
            throw new InvalidDataException("图片 Handler 返回的完整上游身份与所选目标不一致。");
        if (expectedTaskId is not null && result.TaskId != expectedTaskId)
            throw new InvalidDataException("图片查询返回了其他原生任务。");
        if (result.TaskId is { } taskId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(taskId); Internal.LokiImageSnapshots.Text(taskId);
            result.TaskReference = new(model, result.ProviderId, taskId, fingerprint, result.NativeTaskReference);
        }
        else if (result.State is LokiImageGenerationState.Pending or LokiImageGenerationState.Running)
            throw new InvalidDataException("异步图片回执缺少原生任务 ID。");
        token.ThrowIfCancellationRequested(); return result;
    }
}
