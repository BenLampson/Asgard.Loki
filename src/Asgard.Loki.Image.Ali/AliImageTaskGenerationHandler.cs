using System.Text.Json;
using Asgard.Loki.Image.Ali.Internal;

namespace Asgard.Loki.Image.Ali;

/// <summary>显式现代异步图片生成 Handler，不按模型名称猜测同步或异步服务。</summary>
public sealed class AliImageTaskGenerationHandler : AliImageGenerationHandler, ILokiImageTaskHandler
{
    /// <inheritdoc />
    public override string Name => "ali-image-task-generation";

    /// <inheritdoc />
    public override Task<LokiImageGenerationResult> GenerateAsync(LokiImageInvocation invocation, LokiImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var native = AliImagePayload.Bridge(MapRequest(invocation, request));
        return SubmitAsync(invocation, native, cancellationToken);
    }

    /// <inheritdoc />
    public override Task<LokiImageGenerationResult> GenerateNativeAsync(LokiImageInvocation invocation, LokiNativeImageGenerationRequest request,
        CancellationToken cancellationToken = default)
        => SubmitAsync(invocation, new() { PublicModel = request.Model, Parameters = (System.Text.Json.Nodes.JsonObject)request.Parameters.DeepClone() }, cancellationToken);

    private static async Task<LokiImageGenerationResult> SubmitAsync(LokiImageInvocation invocation, AliNativeImageRequest request, CancellationToken token)
    {
        var task = await Client(invocation).SubmitAsync(request, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); return Result(invocation, task);
    }

    /// <inheritdoc />
    public async Task<LokiImageGenerationResult> GetAsync(LokiImageInvocation invocation, LokiImageTaskReference reference,
        CancellationToken cancellationToken = default)
    {
        var native = reference.NativeReference?.Deserialize<AliImageTaskReference>()
            ?? throw new ArgumentException("阿里图片任务缺少原生绑定引用。", nameof(reference));
        if (native.PublicModel != reference.Model || native.TargetId != reference.ProviderId || native.TaskId != reference.TaskId)
            throw new ArgumentException("阿里原生任务引用与统一引用不符。", nameof(reference));
        var task = await Client(invocation).GetAsync(native, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); return Result(invocation, task);
    }

    private static AliImageTaskClient Client(LokiImageInvocation invocation)
    {
        var target = invocation.Provider;
        return new(invocation.HttpClientFactory, new()
        {
            Id = target.Id, Model = target.UpstreamModel, ApiKey = target.ApiKey,
            BaseAddress = target.BaseAddress, HttpClientName = target.HttpClientName
        }, AliImageTaskOperation.ImageGeneration);
    }

    private static LokiImageGenerationResult Result(LokiImageInvocation invocation, AliImageTask task)
    {
        var state = task.Status switch
        {
            "PENDING" => LokiImageGenerationState.Pending,
            "RUNNING" => LokiImageGenerationState.Running,
            "SUCCEEDED" => task.Images.Count > 0 ? LokiImageGenerationState.Succeeded : LokiImageGenerationState.NoImage,
            "FAILED" => LokiImageGenerationState.Failed,
            "CANCELED" => LokiImageGenerationState.Canceled,
            _ => LokiImageGenerationState.Unknown
        };
        var native = new ImageGenerationResult(task.Data,
            new(task.Reference.PublicModel, invocation.Provider.UpstreamModel, invocation.Provider.Id), task.Images)
            { RawResponse = task.RawResponse };
        return new(native, state, task.Reference.TaskId, JsonSerializer.SerializeToElement(task.Reference));
    }
}
